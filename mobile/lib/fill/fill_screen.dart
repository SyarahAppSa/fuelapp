import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:geolocator/geolocator.dart';
import 'package:image_picker/image_picker.dart';

import '../api.dart';
import '../theme.dart';
import 'outbox.dart';
import 'stamp.dart';

const reportStatusLabel = {'Draft': 'قيد التوثيق', 'Submitted': 'تم إرسال التقرير', 'NeedsReview': 'بحاجة إلى مراجعة', 'Returned': 'أُعيد إليك', 'Approved': 'معتمد'};
Color reportStatusColor(String s) => switch (s) { 'Approved' => Brand.ok, 'NeedsReview' || 'Returned' => Brand.crit, 'Submitted' => Brand.cyan, _ => Brand.brand };
const tankStates = ['فارغ', 'متبقٍ فيه وقود', 'ممتلئ'];
const locationReasons = ['خدمة الموقع مغلقة في الجهاز', 'لا توجد إشارة GPS في المكان', 'لم يُمنح التطبيق إذن الموقع'];

/// Where the photo was taken. Replaceable in tests (no GPS there).
class FillLocation {
  static Future<({double lat, double lng})?> Function() current = _gps;
  static Future<({double lat, double lng})?> _gps() async {
    try {
      if (!await Geolocator.isLocationServiceEnabled()) return null;
      var perm = await Geolocator.checkPermission();
      if (perm == LocationPermission.denied) perm = await Geolocator.requestPermission();
      if (perm == LocationPermission.denied || perm == LocationPermission.deniedForever) return null;
      final p = await Geolocator.getCurrentPosition(locationSettings: const LocationSettings(accuracy: LocationAccuracy.high, timeLimit: Duration(seconds: 10)));
      return (lat: p.latitude, lng: p.longitude);
    } catch (_) {
      return null;
    }
  }
}

// ── List: trips assigned to me and their report ─────────────────────────────────────────────────────────────────

class FillReportsTab extends StatefulWidget {
  const FillReportsTab({super.key});
  @override
  State<FillReportsTab> createState() => _FillReportsTabState();
}

class _FillReportsTabState extends State<FillReportsTab> {
  List<Map<String, dynamic>> _rows = [];
  bool _loading = true;
  bool _cached = false;

  @override
  void initState() {
    super.initState();
    FillOutbox.instance.load().then((_) => _load());
  }

  Future<void> _load() async {
    setState(() => _loading = true);
    try {
      final r = await Api.instance.get('/api/fill-reports/mine') as List;
      _rows = r.cast<Map<String, dynamic>>();
      _cached = false;
      await DeviceStore.write('mine.json', Uint8List.fromList(utf8.encode(jsonEncode(_rows))));
      unawaited(FillOutbox.instance.sync());
    } catch (e) {
      final raw = await DeviceStore.read('mine.json');   // offline: last list seen on this phone
      if (raw != null) _rows = (jsonDecode(utf8.decode(raw)) as List).cast<Map<String, dynamic>>();
      _cached = true;
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _open(Map<String, dynamic> row) async {
    await Navigator.of(context).push(MaterialPageRoute(builder: (_) => FillReportScreen(tripId: row['tripId'] as String, header: row)));
    _load();
  }

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: FillOutbox.instance,
      builder: (context, _) => RefreshIndicator(
        onRefresh: _load,
        child: ListView(padding: const EdgeInsets.all(16), children: [
          const SyncBar(),
          if (_cached) const Padding(padding: EdgeInsets.only(bottom: 8), child: Text('تعرض آخر قائمة محفوظة على الجهاز.', style: TextStyle(fontSize: 12))),
          if (_loading && _rows.isEmpty) const Padding(padding: EdgeInsets.all(32), child: Center(child: CircularProgressIndicator())),
          if (!_loading && _rows.isEmpty) const Padding(padding: EdgeInsets.all(32), child: Text('لا توجد رحلات مسندة إليك الآن.', textAlign: TextAlign.center)),
          for (final r in _rows) _tripCard(r),
        ]),
      ),
    );
  }

  Widget _tripCard(Map<String, dynamic> row) {
    final ob = FillOutbox.instance;
    final local = ob.reports[row['tripId']];
    final rep = row['report'] as Map<String, dynamic>?;
    final status = local?.server?['status'] as String? ?? rep?['status'] as String?;
    final waiting = ob.pendingFor(row['tripId'] as String) > 0;
    final started = local != null || rep != null;
    final done = (local?.stagesDone.length ?? rep?['stagesDone'] as int? ?? 0);
    return Card(
      margin: const EdgeInsets.only(bottom: 12),
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          Row(children: [
            Expanded(child: Text('${row['tripNo']} · ${row['plate'] ?? ''}', style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16))),
            if (waiting) const _Chip('بانتظار المزامنة', Brand.warn) else if (status != null) _Chip(reportStatusLabel[status] ?? status, reportStatusColor(status)),
          ]),
          const SizedBox(height: 4),
          if (row['orderNo'] != null) Text('طلب العميل ${row['orderNo']}${row['customerRef'] != null ? ' · أمر الشراء ${row['customerRef']}' : ''}', style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 13)),
          Text('${row['customer'] ?? ''} — ${row['site'] ?? ''}'),
          Text('الكمية المطلوبة ${fmt(row['qtyRequested'] as num?)} لتر${started ? ' · المراحل $done/4' : ''}', style: const TextStyle(fontSize: 13)),
          if (rep?['reviewNote'] != null && status == 'Returned') Padding(padding: const EdgeInsets.only(top: 6), child: Text('ملاحظة العمليات: ${rep!['reviewNote']}', style: const TextStyle(color: Brand.crit))),
          const SizedBox(height: 10),
          FilledButton.icon(
            onPressed: () => _open(row),
            icon: Icon(started ? Icons.arrow_back : Icons.play_arrow),
            label: Text(!started ? 'ابدأ التوثيق' : (status == 'Draft' || status == 'Returned' || status == null) ? 'متابعة التوثيق' : 'عرض التقرير'),
          ),
        ]),
      ),
    );
  }
}

class _Chip extends StatelessWidget {
  const _Chip(this.text, this.color);
  final String text;
  final Color color;
  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
        decoration: BoxDecoration(color: color, borderRadius: BorderRadius.circular(20)),
        child: Text(text, style: const TextStyle(color: Colors.white, fontSize: 12, fontWeight: FontWeight.w700)),
      );
}

/// «بانتظار المزامنة» bar: what is still on the phone, and a button to send it now.
class SyncBar extends StatelessWidget {
  const SyncBar({super.key});
  @override
  Widget build(BuildContext context) {
    final ob = FillOutbox.instance;
    return ListenableBuilder(
      listenable: ob,
      builder: (context, _) {
        if (ob.pending == 0 && ob.online) return const SizedBox.shrink();
        return Container(
          margin: const EdgeInsets.only(bottom: 12),
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(color: Brand.warn.withValues(alpha: .12), borderRadius: BorderRadius.circular(12), border: Border.all(color: Brand.warn)),
          child: Row(children: [
            Icon(ob.online ? Icons.cloud_upload_outlined : Icons.cloud_off, color: Brand.warn),
            const SizedBox(width: 10),
            Expanded(
              child: Text(
                [
                  if (!ob.online) 'لا يوجد اتصال بالإنترنت.',
                  if (ob.pending > 0) 'بانتظار المزامنة: ${ob.pending} ${ob.pending == 1 ? 'عنصر' : 'عناصر'} محفوظة على الجهاز.',
                  if (ob.pending > 0 && !DeviceStore.persistent) 'لا تغلق الصفحة قبل اكتمال المزامنة.',
                ].join(' '),
                style: const TextStyle(fontWeight: FontWeight.w700),
              ),
            ),
            if (ob.pending > 0)
              TextButton(onPressed: ob.syncing ? null : ob.sync, child: Text(ob.syncing ? 'جارٍ الإرسال…' : 'مزامنة الآن')),
          ]),
        );
      },
    );
  }
}

// ── One report: 4 stages + review ───────────────────────────────────────────────────────────────────────────────

class FillReportScreen extends StatefulWidget {
  const FillReportScreen({super.key, required this.tripId, required this.header});
  final String tripId;
  final Map<String, dynamic> header;
  @override
  State<FillReportScreen> createState() => _FillReportScreenState();
}

class _FillReportScreenState extends State<FillReportScreen> {
  final ob = FillOutbox.instance;
  late LocalReport r;
  int _step = 0;
  bool _busy = false;
  String? _localError;
  final _c = <String, TextEditingController>{};
  String _outcome = 'Full';
  String? _tankState;

  TextEditingController c(String k) => _c.putIfAbsent(k, () => TextEditingController(text: _initial(k)));
  String _initial(String k) {
    final v = r.inputs[k];
    if (v == null) return '';
    return v is num ? fmt(v).replaceAll(',', '') : '$v';
  }

  @override
  void initState() {
    super.initState();
    r = ob.report(widget.tripId, header: widget.header);
    _init();
  }

  Future<void> _init() async {
    await ob.load();
    await ob.start(widget.tripId, widget.header);   // safe to repeat; offline it waits in the queue
    await ob.refresh(widget.tripId);
    if (!mounted) return;
    setState(() {
      _outcome = r.inputs['outcome'] as String? ?? (r.server?['stages']?['postUnload']?['outcome'] as String?) ?? 'Full';
      _tankState = r.inputs['tankState'] as String?;
      _step = r.locked || r.submitted ? 4 : stages.indexWhere((s) => !r.stagesDone.contains(s)).clamp(0, 4);
      if (stages.every(r.stagesDone.contains)) _step = 4;
    });
  }

  @override
  void dispose() {
    for (final x in _c.values) {
      x.dispose();
    }
    super.dispose();
  }

  bool get editable => !r.locked && !(r.submitted && ob.pendingFor(widget.tripId) > 0);

  Map<String, dynamic> get _info {
    final s = r.server;
    final h = widget.header;
    return {
      'no': s?['no'], 'tripNo': s?['tripNo'] ?? h['tripNo'], 'plate': s?['plate'] ?? h['plate'], 'trailerPlate': s?['trailerPlate'],
      'customer': s?['customerName'] ?? h['customer'], 'site': s?['deliverySite'] ?? h['site'], 'loadingSite': s?['loadingSite'] ?? h['loadingSite'],
      'orderNo': s?['orderNo'] ?? h['orderNo'], 'customerRef': s?['customerRef'] ?? h['customerRef'],
      'product': s?['product'] ?? h['product'], 'requested': s?['quantities']?['requested'] ?? h['qtyRequested'], 'driver': s?['driver'],
    };
  }

  // ── camera ──
  Future<void> _take(Slot slot) async {
    if (slot.meter) {
      final go = await showDialog<bool>(
        context: context,
        builder: (_) => AlertDialog(
          title: const Text('صورة العداد'),
          content: const Text('حتى تُقبل القراءة:\n• اقترب حتى تملأ أرقام العداد الصورة.\n• صوّر بشكل مستقيم بلا ميلان.\n• تجنّب انعكاس الضوء والظل على الزجاج.\n• تأكد أن الرقم التسلسلي للعداد ظاهر إن أمكن.'),
          actions: [TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('إلغاء')), FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('افتح الكاميرا'))],
        ),
      );
      if (go != true) return;
    }
    while (true) {
      // camera only: the app never offers the gallery
      final x = await ImagePicker().pickImage(source: ImageSource.camera, imageQuality: 85, maxWidth: 1920, preferredCameraDevice: CameraDevice.rear);
      if (x == null || !mounted) return;
      final at = DateTime.now();
      final raw = await x.readAsBytes();
      if (!mounted) return;
      setState(() => _busy = true);
      final loc = await FillLocation.current();
      String? note;
      if (loc == null && mounted) note = await _askLocationReason() ?? 'تعذر تحديد الموقع';
      Uint8List bytes;
      try {
        // date, time and place burnt into the picture itself
        bytes = await PhotoStamp.apply(raw, [
          '${slot.label} · ${PhotoStamp.ltr('${_info['tripNo'] ?? ''}')}',
          'التاريخ والوقت: ${PhotoStamp.when(at)}',
          loc != null ? 'الموقع: ${PhotoStamp.ltr('${loc.lat.toStringAsFixed(5)}, ${loc.lng.toStringAsFixed(5)}')}' : 'الموقع: غير متاح — $note',
        ]);
      } catch (_) {
        bytes = raw;   // never lose the evidence because the stamp failed; time and place still travel with the upload
      }
      if (!mounted) return;
      setState(() => _busy = false);
      final use = await showDialog<bool>(
        context: context,
        barrierDismissible: false,
        builder: (_) => AlertDialog(
          title: Text(slot.label),
          content: ConstrainedBox(constraints: const BoxConstraints(maxHeight: 460), child: Image.memory(bytes, fit: BoxFit.contain)),
          actions: [
            TextButton.icon(onPressed: () => Navigator.pop(context, false), icon: const Icon(Icons.refresh), label: const Text('إعادة التصوير')),
            FilledButton.icon(onPressed: () => Navigator.pop(context, true), icon: const Icon(Icons.check), label: const Text('استخدم الصورة')),
          ],
        ),
      );
      if (use == null || !mounted) return;
      if (!use) continue;
      await ob.addPhoto(widget.tripId, bytes, kind: slot.kind, capturedAt: at, lat: loc?.lat, lng: loc?.lng, locationNote: loc == null ? note : null);
      if (mounted) setState(() => _localError = null);
      return;
    }
  }

  Future<String?> _askLocationReason() => showModalBottomSheet<String>(
        context: context,
        showDragHandle: true,
        isDismissible: false,
        builder: (_) => SafeArea(
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            const Padding(padding: EdgeInsets.all(12), child: Text('تعذر تحديد الموقع. ما السبب؟', style: TextStyle(fontWeight: FontWeight.w800, fontSize: 16))),
            for (final x in locationReasons) ListTile(title: Text(x), onTap: () => Navigator.pop(context, x)),
          ]),
        ),
      );

  // ── stage confirmation (validated here for a quick answer; the server checks everything again) ──
  String? _missingFor(String stage) {
    final failed = stage == 'post-unload' && _outcome == 'Failed';
    for (final s in slots.where((s) => s.stage == stage)) {
      if (failed) break;
      if (!r.photos.containsKey(s.kind) && !_serverHas(s.kind)) return s.missing;
    }
    switch (stage) {
      case 'post-fill':
        if ((parseNum(c('loadedQty').text) ?? 0) <= 0) return 'أدخل الكمية المحمّلة باللتر';
      case 'arrival':
        if (parseNum(c('meterBefore').text) == null) return 'أدخل قراءة العداد قبل التفريغ';
      case 'post-unload':
        if (_outcome != 'Full' && c('outcomeReason').text.trim().isEmpty) return _outcome == 'Partial' ? 'اكتب سبب التفريغ الجزئي' : 'اكتب سبب تعذّر التفريغ';
        if (!failed && parseNum(c('meterAfter').text) == null) return 'أدخل قراءة العداد بعد التفريغ';
        if (_tankState == null) return 'حدّد حالة الصهريج بعد التفريغ';
    }
    return null;
  }

  bool _serverHas(String kind) => ((r.server?['photos'] as List?) ?? []).any((p) => p['kind'] == kind && p['id'] != null);

  Future<void> _confirm(String stage) async {
    final miss = _missingFor(stage);
    if (miss != null) return setState(() => _localError = miss);
    final body = <String, dynamic>{};
    switch (stage) {
      case 'post-fill':
        body['loadedQty'] = parseNum(c('loadedQty').text);
      case 'arrival':
        body['meterBefore'] = parseNum(c('meterBefore').text);
        if (c('meterSerial').text.trim().isNotEmpty) body['meterSerial'] = c('meterSerial').text.trim();
      case 'post-unload':
        body['outcome'] = _outcome;
        body['tankState'] = _tankState;
        if (parseNum(c('meterAfter').text) case final v?) body['meterAfter'] = v;
        if (_outcome != 'Full') body['outcomeReason'] = c('outcomeReason').text.trim();
    }
    setState(() { _busy = true; _localError = null; });
    await ob.confirmStage(widget.tripId, stage, body);
    if (mounted) setState(() { _busy = false; _step = (_step + 1).clamp(0, 4); });
  }

  Future<void> _submit() async {
    final miss = _allMissing();
    if (miss.isNotEmpty) return setState(() => _localError = miss.first);
    setState(() { _busy = true; _localError = null; });
    await ob.submit(widget.tripId);
    if (!mounted) return;
    setState(() => _busy = false);
    if (r.locked) {
      await showDialog<void>(
        context: context,
        builder: (_) => AlertDialog(
          icon: Icon(r.status == 'NeedsReview' ? Icons.report_gmailerrorred : Icons.check_circle, color: r.status == 'NeedsReview' ? Brand.warn : Brand.ok, size: 48),
          title: const Text('تم إرسال التقرير'),
          content: Text('رقم التقرير: ${r.server?['no']}${r.status == 'NeedsReview' ? '\n\nبحاجة إلى مراجعة من فريق العمليات:\n${r.server?['reviewReasons'] ?? ''}' : ''}', textAlign: TextAlign.center),
          actions: [FilledButton(onPressed: () => Navigator.pop(context), child: const Text('تم'))],
        ),
      );
    }
  }

  List<String> _allMissing() {
    final out = <String>[];
    final failed = _outcome == 'Failed';
    for (final s in slots) {
      if (failed && s.stage == 'post-unload') continue;
      if (!r.photos.containsKey(s.kind) && !_serverHas(s.kind)) out.add(s.missing);
    }
    for (final s in stages) {
      if (!r.stagesDone.contains(s)) out.add('أكّد مرحلة «${stageTitles[s]}»');
    }
    return out;
  }

  // ── UI ──
  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: ob,
      builder: (context, _) => Scaffold(
        appBar: AppBar(title: Text(r.server?['no'] as String? ?? 'تقرير التعبئة')),
        body: SafeArea(
          child: ListView(padding: const EdgeInsets.all(16), children: [
            const SyncBar(),
            _headerCard(),
            const SizedBox(height: 12),
            _stepper(),
            const SizedBox(height: 12),
            if (r.status == 'Returned' && r.server?['reviewNote'] != null) _banner('أعاد فريق العمليات التقرير: ${r.server!['reviewNote']}', Brand.crit),
            if (r.lastError != null) _banner(r.lastError!, Brand.crit),
            if (_localError != null) _banner(_localError!, Brand.crit),
            if (_step < 4) _stageBody(stages[_step]) else _review(),
          ]),
        ),
      ),
    );
  }

  Widget _banner(String text, Color color) => Container(
        margin: const EdgeInsets.only(bottom: 12),
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(color: color.withValues(alpha: .1), borderRadius: BorderRadius.circular(12), border: Border.all(color: color)),
        child: Text(text, style: TextStyle(color: color, fontWeight: FontWeight.w700)),
      );

  Widget _headerCard() {
    final i = _info;
    final d = i['driver'] as Map<String, dynamic>?;
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(gradient: Brand.heroGradient, borderRadius: BorderRadius.circular(16)),
      child: DefaultTextStyle(
        style: const TextStyle(color: Colors.white, fontFamily: 'Tajawal', fontSize: 14),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text('الرحلة ${i['tripNo'] ?? ''}${i['no'] != null ? ' · التقرير ${i['no']}' : ''}', style: const TextStyle(fontWeight: FontWeight.w700)),
          const SizedBox(height: 4),
          Text('${i['plate'] ?? ''}${i['trailerPlate'] != null ? '  ·  مقطورة ${i['trailerPlate']}' : ''}', style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w800)),
          Text('${i['customer'] ?? ''}'),
          if (i['orderNo'] != null) Text('طلب العميل ${i['orderNo']}${i['customerRef'] != null ? ' · أمر الشراء ${i['customerRef']}' : ''}', style: const TextStyle(fontWeight: FontWeight.w700)),
          Text('من ${i['loadingSite'] ?? '—'} إلى ${i['site'] ?? '—'}'),
          Text('${i['product'] ?? ''} · المطلوب ${fmt(i['requested'] as num?)} لتر'),
          if (d != null) Text('السائق ${d['name']} · الهوية ${d['nationalId'] ?? '—'} · الجوال ${d['phone'] ?? '—'}', style: const TextStyle(fontSize: 12)),
        ]),
      ),
    );
  }

  Widget _stepper() {
    final titles = [...stages.map((s) => stageTitles[s]!), 'المراجعة'];
    return Row(children: [
      for (var k = 0; k < titles.length; k++)
        Expanded(
          child: InkWell(
            onTap: () => setState(() { _step = k; _localError = null; }),
            child: Column(children: [
              CircleAvatar(
                radius: 16,
                backgroundColor: k == _step ? Brand.brand : (k < 4 && r.stagesDone.contains(stages[k])) ? Brand.ok : Colors.grey.shade400,
                child: (k < 4 && r.stagesDone.contains(stages[k]) && k != _step) ? const Icon(Icons.check, size: 18, color: Colors.white) : Text('${k + 1}', style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w800)),
              ),
              const SizedBox(height: 4),
              Text(titles[k], style: TextStyle(fontSize: 11, fontWeight: k == _step ? FontWeight.w800 : FontWeight.w400), textAlign: TextAlign.center),
            ]),
          ),
        ),
    ]);
  }

  Widget _photoSlot(Slot s, {bool optional = false}) {
    final p = r.photos[s.kind];
    final serverPhoto = ((r.server?['photos'] as List?) ?? []).cast<Map<String, dynamic>>().where((x) => x['kind'] == s.kind && x['id'] != null).firstOrNull;
    final has = p != null || serverPhoto != null;
    return Card(
      margin: const EdgeInsets.only(bottom: 12),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          Text(s.label + (optional ? ' (اختياري عند تعذّر التفريغ)' : ''), style: const TextStyle(fontWeight: FontWeight.w800)),
          const SizedBox(height: 8),
          if (has) ...[
            ClipRRect(borderRadius: BorderRadius.circular(10), child: SizedBox(height: 170, child: PhotoThumb(local: p, serverId: p?.serverId ?? serverPhoto?['id'] as String?))),
            const SizedBox(height: 6),
            Text(_photoMeta(p, serverPhoto), style: const TextStyle(fontSize: 12)),
            if (editable) TextButton.icon(onPressed: () => _take(s), icon: const Icon(Icons.refresh), label: const Text('إعادة التصوير')),
          ] else if (editable)
            SizedBox(
              height: 72,
              child: FilledButton.icon(onPressed: () => _take(s), icon: const Icon(Icons.photo_camera, size: 30), label: Text('صوّر: ${s.label}', style: const TextStyle(fontSize: 16))),
            )
          else
            const Text('لم تُلتقط', style: TextStyle(color: Brand.crit)),
        ]),
      ),
    );
  }

  String _photoMeta(LocalPhoto? p, Map<String, dynamic>? sp) {
    final at = p?.capturedAt ?? DateTime.tryParse(sp?['capturedAt'] as String? ?? '')?.toLocal();
    final time = at == null ? '' : '${at.toLocal().toString().substring(0, 16)} · ';
    final lat = p?.lat ?? (sp?['lat'] as num?)?.toDouble();
    final lng = p?.lng ?? (sp?['lng'] as num?)?.toDouble();
    final where = lat != null && lng != null ? '📍 ${lat.toStringAsFixed(4)}, ${lng.toStringAsFixed(4)}' : '📍 ${p?.locationNote ?? sp?['locationNote'] ?? 'بدون موقع'}';
    final sync = p == null || p.synced ? ' · ✓ أُرسلت' : ' · ⏳ بانتظار المزامنة';
    return '$time$where$sync';
  }

  Widget _field(String key, String label, {bool number = true, String? help}) => Padding(
        padding: const EdgeInsets.only(bottom: 12),
        child: TextField(
          controller: c(key),
          enabled: editable,
          keyboardType: number ? const TextInputType.numberWithOptions(decimal: true) : TextInputType.text,
          decoration: InputDecoration(labelText: label, helperText: help),
          onChanged: (_) => setState(() {}),
        ),
      );

  Widget _stageBody(String stage) {
    final failed = stage == 'post-unload' && _outcome == 'Failed';
    final before = parseNum(c('meterBefore').text), after = parseNum(c('meterAfter').text);
    return Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      Text('${_step + 1}. ${stageTitles[stage]}', style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w800)),
      const SizedBox(height: 8),
      if (stage == 'post-unload') ...[
        const Text('نتيجة التفريغ', style: TextStyle(fontWeight: FontWeight.w700)),
        const SizedBox(height: 6),
        SegmentedButton<String>(
          segments: const [ButtonSegment(value: 'Full', label: Text('كامل')), ButtonSegment(value: 'Partial', label: Text('جزئي')), ButtonSegment(value: 'Failed', label: Text('تعذّر'))],
          selected: {_outcome},
          onSelectionChanged: editable ? (v) => setState(() => _outcome = v.first) : null,
        ),
        const SizedBox(height: 12),
        if (_outcome != 'Full') _field('outcomeReason', _outcome == 'Partial' ? 'سبب التفريغ الجزئي' : 'سبب تعذّر التفريغ', number: false, help: 'صوّر ما يثبت السبب إن أمكن (مثل امتلاء خزان العميل).'),
      ],
      for (final s in slots.where((s) => s.stage == stage)) _photoSlot(s, optional: failed),
      if (stage == 'post-fill') _field('loadedQty', 'الكمية المحمّلة (لتر)', help: r.server?['stages']?['postFill']?['loadedFromTrip'] == true ? 'من سند التحميل؛ عدّلها إن اختلفت.' : null),
      if (stage == 'arrival') ...[
        _field('meterSerial', 'الرقم التسلسلي للعداد السري', number: false),
        _field('meterBefore', 'قراءة العداد قبل التفريغ'),
      ],
      if (stage == 'post-unload') ...[
        if (!failed) _field('meterAfter', 'قراءة العداد بعد التفريغ'),
        DropdownButtonFormField<String>(
          initialValue: _tankState,
          decoration: const InputDecoration(labelText: 'حالة الصهريج بعد التفريغ'),
          items: [for (final t in tankStates) DropdownMenuItem(value: t, child: Text(t))],
          onChanged: editable ? (v) => setState(() => _tankState = v) : null,
        ),
        const SizedBox(height: 12),
        if (before != null && after != null)
          _banner(after < before ? 'القراءة بعد التفريغ أقل من القراءة قبله؛ تحقق منها. إن كانت صحيحة سيُرسل التقرير للمراجعة.' : 'الكمية المسلّمة حسب العداد: ${fmt(after - before)} لتر',
              after < before ? Brand.warn : Brand.ok),
      ],
      if (editable)
        SizedBox(height: 56, child: FilledButton(onPressed: _busy ? null : () => _confirm(stage), child: Text(_busy ? 'جارٍ الحفظ…' : 'تأكيد ومتابعة', style: const TextStyle(fontSize: 17)))),
    ]);
  }

  Widget _review() {
    final q = (r.server?['quantities'] as Map?)?.cast<String, dynamic>() ?? {};
    final loaded = parseNum(c('loadedQty').text) ?? q['loaded'] as num?;
    final before = parseNum(c('meterBefore').text), after = parseNum(c('meterAfter').text);
    final delivered = before != null && after != null && after >= before ? after - before : q['delivered'] as num?;
    final diff = delivered != null && loaded != null ? delivered - loaded : null;
    final missing = _allMissing();
    final waiting = ob.pendingFor(widget.tripId) > 0;
    Widget kv(String k, String v, {Color? color}) => Padding(padding: const EdgeInsets.symmetric(vertical: 3), child: Row(children: [Expanded(child: Text(k)), Text(v, style: TextStyle(fontWeight: FontWeight.w800, color: color))]));
    return Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      const Text('ملخص التقرير', style: TextStyle(fontSize: 20, fontWeight: FontWeight.w800)),
      const SizedBox(height: 8),
      if (r.locked)
        _banner('${r.status == 'Approved' ? 'معتمد' : 'تم إرسال التقرير'} · رقم التقرير ${r.server?['no']}${r.status == 'NeedsReview' ? '\nبحاجة إلى مراجعة: ${r.server?['reviewReasons'] ?? ''}' : ''}', r.status == 'NeedsReview' ? Brand.warn : Brand.ok),
      if (r.submitted && waiting) _banner('بانتظار المزامنة: سيُرسل التقرير تلقائيًا عند عودة الاتصال.', Brand.warn),
      GridView.count(
        crossAxisCount: 3,
        shrinkWrap: true,
        physics: const NeverScrollableScrollPhysics(),
        mainAxisSpacing: 6,
        crossAxisSpacing: 6,
        children: [
          for (final s in slots)
            Column(children: [
              Expanded(
                child: ClipRRect(
                  borderRadius: BorderRadius.circular(8),
                  child: r.photos[s.kind] != null || _serverHas(s.kind)
                      ? PhotoThumb(local: r.photos[s.kind], serverId: r.photos[s.kind]?.serverId ?? ((r.server?['photos'] as List?) ?? []).cast<Map>().where((x) => x['kind'] == s.kind).firstOrNull?['id'] as String?)
                      : Container(color: Colors.grey.shade300, child: const Center(child: Icon(Icons.no_photography, color: Brand.crit))),
                ),
              ),
              Text(s.label, style: const TextStyle(fontSize: 10), textAlign: TextAlign.center, maxLines: 2),
            ]),
        ],
      ),
      const SizedBox(height: 12),
      Card(
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Column(children: [
            kv('الكمية المطلوبة', '${fmt(_info['requested'] as num?)} لتر'),
            kv('الكمية المحمّلة', '${fmt(loaded)} لتر'),
            kv('العداد قبل التفريغ', fmt(before)),
            kv('العداد بعد التفريغ', fmt(after)),
            kv('الكمية المسلّمة (بعد − قبل)', '${fmt(delivered)} لتر'),
            kv('الفرق عن المحمّل', diff == null ? '—' : '${diff > 0 ? '+' : ''}${fmt(diff)} لتر', color: diff != null && diff.abs() > (q['allowed'] as num? ?? 0) && q['allowed'] != null ? Brand.crit : null),
            kv('نتيجة التفريغ', {'Full': 'كامل', 'Partial': 'جزئي', 'Failed': 'تعذّر'}[_outcome]!),
            kv('حالة الصهريج', _tankState ?? '—'),
          ]),
        ),
      ),
      const SizedBox(height: 12),
      if (missing.isNotEmpty && !r.locked) ...[
        const Text('ناقص قبل الإرسال:', style: TextStyle(fontWeight: FontWeight.w800, color: Brand.crit)),
        for (final m in missing) Text('• $m', style: const TextStyle(color: Brand.crit)),
        const SizedBox(height: 12),
      ],
      if (editable)
        SizedBox(
          height: 56,
          child: FilledButton.icon(
            onPressed: _busy || missing.isNotEmpty ? null : _submit,
            icon: const Icon(Icons.send),
            label: Text(_busy ? 'جارٍ الإرسال…' : 'إرسال التقرير', style: const TextStyle(fontSize: 17)),
          ),
        ),
    ]);
  }
}

/// A photo from the phone (kept until sent) or, once sent, from the server with the driver's session.
class PhotoThumb extends StatefulWidget {
  const PhotoThumb({super.key, this.local, this.serverId});
  final LocalPhoto? local;
  final String? serverId;
  @override
  State<PhotoThumb> createState() => _PhotoThumbState();
}

class _PhotoThumbState extends State<PhotoThumb> {
  Uint8List? _bytes;
  String? _key;

  @override
  Widget build(BuildContext context) {
    final key = '${widget.local?.clientId}|${widget.serverId}';
    if (key != _key) {
      _key = key;
      _bytes = null;
      _fetch(key);
    }
    return _bytes == null
        ? Container(color: Colors.grey.shade200, child: const Center(child: Icon(Icons.image_outlined)))
        : Image.memory(_bytes!, fit: BoxFit.cover, width: double.infinity, gaplessPlayback: true);
  }

  Future<void> _fetch(String key) async {
    Uint8List? b;
    if (widget.local != null) b = await DeviceStore.read('${widget.local!.clientId}.jpg');
    if (b == null && widget.serverId != null) {
      try {
        b = await Api.instance.photoBytes(widget.serverId!);
      } catch (_) {}
    }
    if (mounted && key == _key && b != null) setState(() => _bytes = b);
  }
}
