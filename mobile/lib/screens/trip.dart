import 'package:flutter/material.dart';
import 'package:image_picker/image_picker.dart';

import '../api.dart';
import '../theme.dart';

/// One trip, step by step. Each step is a server call; the server enforces the order
/// and every business rule (meter readings, assignment, reconciliation). The app shows
/// the server's rejection message as is, so the driver sees exactly why.
class TripScreen extends StatefulWidget {
  const TripScreen({super.key, required this.tripId, required this.me});
  final String tripId;
  final Me me;
  @override
  State<TripScreen> createState() => _TripScreenState();
}

class _TripScreenState extends State<TripScreen> {
  TripView? _t;
  String? _error;
  bool _busy = false;
  final _photos = <String, XFile>{};
  final _f = <String, TextEditingController>{};
  bool _sealsOk = true;
  bool _moreStops = false;
  String? _vehicleId;

  TextEditingController c(String k) => _f.putIfAbsent(k, TextEditingController.new);
  num? n(String k) => parseNum(c(k).text);

  @override
  void initState() {
    super.initState();
    _reload();
  }

  Future<void> _reload() async {
    final r = await Api.instance.get('/api/trips/${widget.tripId}') as Map<String, dynamic>;
    setState(() {
      _t = TripView(r['view'] as Map<String, dynamic>);
      _vehicleId ??= _t!.vehicleId ?? (widget.me.vehicles.isNotEmpty ? widget.me.vehicles.first.id : null);
    });
  }

  Future<void> _photo(String kind) async {
    final x = await ImagePicker().pickImage(source: ImageSource.camera, imageQuality: 70);
    if (x != null) setState(() => _photos[kind] = x);
  }

  Future<void> _run(String step, Map<String, dynamic> body, {List<String> photos = const []}) async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      for (final k in photos) {
        if (_photos[k] == null) throw ApiException(400, 'صورة «${_photoLabel[k]}» مطلوبة.');
      }
      final res = await Api.instance.post('/api/trips/${widget.tripId}/$step', body) as Map<String, dynamic>;
      for (final k in photos) {
        await Api.instance.upload(_photos[k]!, ownerType: 'trip', ownerId: widget.tripId, kind: k);
      }
      _photos.clear();
      setState(() => _t = TripView(res));
    } catch (e) {
      setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  static const _photoLabel = {
    'load-doc': 'سند التحميل',
    'meter-start': 'العداد قبل التعبئة',
    'meter-end': 'العداد بعد التعبئة',
    'seals': 'الأختام',
    'unload-meter-before': 'عداد التفريغ قبل',
    'unload-meter-after': 'عداد التفريغ بعد',
    'receipt': 'سند الاستلام الموقّع',
  };

  @override
  Widget build(BuildContext context) {
    final t = _t;
    return Scaffold(
      appBar: AppBar(title: Text(t?.no ?? 'الطلب')),
      body: t == null
          ? const Center(child: CircularProgressIndicator())
          : RefreshIndicator(
              onRefresh: _reload,
              child: ListView(padding: const EdgeInsets.all(16), children: [
                _header(t),
                const SizedBox(height: 12),
                if (_error != null) _errorBox(_error!),
                ..._step(t),
              ]),
            ),
    );
  }

  Widget _header(TripView t) => Container(
        padding: const EdgeInsets.all(16),
        decoration: BoxDecoration(gradient: Brand.heroGradient, borderRadius: BorderRadius.circular(14)),
        child: DefaultTextStyle(
          style: const TextStyle(color: Colors.white),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(t.label, style: const TextStyle(fontSize: 13)),
            const SizedBox(height: 4),
            Text(t.customer, style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w800)),
            Text('${t.station} ← ${t.site}'),
            const SizedBox(height: 8),
            Text('المطلوب ${fmt(t.qtyRequested)} لتر · المحمّل ${fmt(t.rec['loaded'] as num?)} · المسلّم ${fmt(t.rec['delivered'] as num?)}'),
          ]),
        ),
      );

  Widget _errorBox(String msg) => Container(
        margin: const EdgeInsets.only(bottom: 12),
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(color: Brand.crit.withValues(alpha: .1), border: Border.all(color: Brand.crit), borderRadius: BorderRadius.circular(10)),
        child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
          const Icon(Icons.block, color: Brand.crit),
          const SizedBox(width: 8),
          Expanded(child: Text(msg, style: const TextStyle(color: Brand.crit, fontWeight: FontWeight.w600))),
        ]),
      );

  Widget _field(String k, String label, {bool number = true}) => Padding(
        padding: const EdgeInsets.only(bottom: 10),
        child: TextField(controller: c(k), keyboardType: number ? const TextInputType.numberWithOptions(decimal: true) : TextInputType.text, decoration: InputDecoration(labelText: label)),
      );

  Widget _photoBtn(String kind) => Padding(
        padding: const EdgeInsets.only(bottom: 8),
        child: OutlinedButton.icon(
          onPressed: () => _photo(kind),
          icon: Icon(_photos[kind] == null ? Icons.photo_camera_outlined : Icons.check_circle, color: _photos[kind] == null ? null : Brand.ok),
          label: Text('صورة ${_photoLabel[kind]}'),
        ),
      );

  Widget _submit(String label, VoidCallback onPressed) => Padding(padding: const EdgeInsets.only(top: 8), child: FilledButton(onPressed: _busy ? null : onPressed, child: Text(_busy ? 'جارٍ الإرسال…' : label)));

  Widget _title(String s) => Padding(padding: const EdgeInsets.only(bottom: 10), child: Text(s, style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w800)));

  /// A trip handed over to me while loaded: I confirm (or dispute) the quantity and meter before continuing.
  Map<String, dynamic>? _pendingHandover(TripView t) {
    for (final h in t.handovers.reversed) {
      if (h['toDriverId'] == widget.me.driverId && h['ack'] == 'Pending') return h;
    }
    return null;
  }

  List<Widget> _ackStep(Map<String, dynamic> h) => [
        _title('تأكيد استلام الطلب'),
        Text('نُقل إليك هذا الطلب. الكمية المتبقية: ${fmt(h['remaining'] as num?)} لتر · قراءة العداد: ${fmt(h['meterReading'] as num?)}\nالسبب: ${h['note'] ?? ''}'),
        const SizedBox(height: 10),
        _field('ackNote', 'ملاحظة (مطلوبة عند عدم المطابقة)', number: false),
        _submit('أؤكد الاستلام والمطابقة', () => _run('handover/ack', {'confirmed': true, 'note': c('ackNote').text.trim()})),
        const SizedBox(height: 8),
        OutlinedButton(
          onPressed: _busy ? null : () => _run('handover/ack', {'confirmed': false, 'note': c('ackNote').text.trim()}),
          child: const Text('الكمية أو العداد غير مطابق'),
        ),
      ];

  List<Widget> _step(TripView t) {
    final pending = _pendingHandover(t);
    if (pending != null) return _ackStep(pending);
    switch (t.status) {
      case 'WaitingLoad':
        final hasMeter = t.meterSerial != null;
        return [
          _title('التحميل'),
          DropdownButtonFormField<String>(
            initialValue: _vehicleId,
            decoration: const InputDecoration(labelText: 'المركبة'),
            items: [for (final v in widget.me.vehicles) DropdownMenuItem(value: v.id, child: Text(v.plate))],
            onChanged: (v) => setState(() => _vehicleId = v),
          ),
          const SizedBox(height: 10),
          _field('qty', 'الكمية المحمّلة (لتر)'),
          _field('doc', 'رقم سند التحميل', number: false),
          _field('odo', 'قراءة عداد الكيلومترات'),
          if (hasMeter) ...[
            Text('عداد التعبئة ${t.meterSerial} — تراكمي: لا تُقبل قراءة أقل من آخر قراءة معتمدة.', style: Theme.of(context).textTheme.bodySmall),
            const SizedBox(height: 8),
            _field('mBase', 'القراءة التأسيسية (أول استخدام للعداد فقط)'),
            _field('mStart', 'قراءة العداد قبل التعبئة'),
            _field('mEnd', 'قراءة العداد بعد التعبئة'),
            _photoBtn('meter-start'),
            _photoBtn('meter-end'),
          ],
          _field('seals', 'أرقام أختام المخارج', number: false),
          _photoBtn('load-doc'),
          _photoBtn('seals'),
          _submit('تأكيد التحميل', () {
            _run('load', {
              'vehicleId': _vehicleId,
              'qty': n('qty') ?? 0,
              'docNo': c('doc').text.trim(),
              'odometer': n('odo') ?? 0,
              'meterBaseline': n('mBase'),
              'meterStart': n('mStart'),
              'meterEnd': n('mEnd'),
              'outletSeals': c('seals').text.trim(),
              'capturedAt': DateTime.now().toUtc().toIso8601String(),
            }, photos: ['load-doc', if (hasMeter) ...['meter-start', 'meter-end']]);
          }),
        ];
      case 'Loaded':
        return [_title('جاهز للانطلاق'), const Text('تم توثيق التحميل. ابدأ الرحلة عند المغادرة.'), _submit('بدء الرحلة', () => _run('depart', {}))];
      case 'EnRoute':
        return [
          _title('في الطريق'),
          _field('loc', 'موقع الوصول (اسم الموقع)', number: false),
          _submit('تأكيد الوصول', () => _run('arrive', {'location': c('loc').text.trim().isEmpty ? t.site : c('loc').text.trim()})),
        ];
      case 'Arrived':
        return [
          _title('فحص الأختام قبل التفريغ'),
          SwitchListTile(value: _sealsOk, onChanged: (v) => setState(() => _sealsOk = v), title: const Text('الأختام سليمة')),
          if (!_sealsOk) _field('sealNotes', 'وصف حالة الأختام', number: false),
          _photoBtn('seals'),
          _submit('بدء التفريغ', () => _run('unload', {'sealsOk': _sealsOk, 'sealNotes': c('sealNotes').text}, photos: ['seals'])),
        ];
      case 'Unloading':
        return [
          _title('التسليم'),
          _field('dBefore', 'عداد التفريغ قبل'),
          _field('dAfter', 'عداد التفريغ بعد'),
          _field('received', 'الكمية حسب سند الاستلام (لتر)'),
          _field('receiptNo', 'رقم سند الاستلام', number: false),
          _field('left', 'المتبقي في الخزان (لتر)'),
          _field('returned', 'المُعاد (لتر)'),
          SwitchListTile(value: _moreStops, onChanged: (v) => setState(() => _moreStops = v), title: const Text('توجد نقاط تسليم أخرى')),
          _photoBtn('unload-meter-before'),
          _photoBtn('unload-meter-after'),
          _photoBtn('receipt'),
          _submit('إرسال التسليم', () {
            _run('deliver', {
              'meterBefore': n('dBefore'),
              'meterAfter': n('dAfter'),
              'received': n('received') ?? 0,
              'receiptNo': c('receiptNo').text.trim(),
              'leftInTank': n('left') ?? 0,
              'returned': n('returned') ?? 0,
              'hasMoreStops': _moreStops,
              'capturedAt': DateTime.now().toUtc().toIso8601String(),
            }, photos: ['unload-meter-before', 'unload-meter-after', 'receipt']);
          }),
        ];
      default:
        return [
          _title(t.label),
          Text(switch (t.status) {
            'AwaitingApproval' => 'تم إرسال الطلب، وهو بانتظار اعتماد المشرف.',
            'UnderReview' => 'يوجد فرق في الكميات؛ الطلب محوّل للمراجعة. سيتواصل معك المشرف عند الحاجة.',
            'Approved' => 'تم اعتماد الطلب.',
            _ => 'لا توجد إجراءات متاحة.',
          }),
        ];
    }
  }
}
