import 'dart:async';
import 'dart:convert';
import 'dart:io' show File, Directory;
import 'dart:math';

import 'package:flutter/foundation.dart';
import 'package:path_provider/path_provider.dart';

import '../api.dart';

/// Photo slots, in report order. Same keys as the server (FillReportsController.Slots).
class Slot {
  const Slot(this.kind, this.stage, this.label, this.missing, {this.meter = false});
  final String kind, stage, label, missing;
  final bool meter;
}

const slots = [
  Slot('tank-pre-fill', 'pre-fill', 'الصهريج قبل التعبئة', 'صوّر الصهريج قبل التعبئة'),
  Slot('tank-post-fill', 'post-fill', 'الصهريج بعد التعبئة', 'صوّر الصهريج بعد التعبئة'),
  Slot('tank-arrival', 'arrival', 'الصهريج ممتلئ عند العميل', 'صوّر الصهريج ممتلئًا عند العميل قبل التفريغ'),
  Slot('meter-before', 'arrival', 'العداد السري قبل التفريغ', 'صوّر قراءة العداد قبل التفريغ', meter: true),
  Slot('tank-post-unload', 'post-unload', 'الصهريج بعد التفريغ', 'صوّر الصهريج بعد التفريغ'),
  Slot('meter-after', 'post-unload', 'العداد السري بعد التفريغ', 'صوّر قراءة العداد بعد التفريغ', meter: true),
];
const stages = ['pre-fill', 'post-fill', 'arrival', 'post-unload'];
const stageTitles = {'pre-fill': 'قبل التعبئة', 'post-fill': 'بعد التعبئة', 'arrival': 'الوصول', 'post-unload': 'بعد التفريغ'};

String newClientId() {
  final r = Random.secure();
  final b = List<int>.generate(16, (_) => r.nextInt(256));
  b[6] = (b[6] & 0x0f) | 0x40;
  b[8] = (b[8] & 0x3f) | 0x80;
  final h = b.map((x) => x.toRadixString(16).padLeft(2, '0')).join();
  return '${h.substring(0, 8)}-${h.substring(8, 12)}-${h.substring(12, 16)}-${h.substring(16, 20)}-${h.substring(20)}';
}

/// A photo taken on the phone. Kept on the device until the server confirms it.
class LocalPhoto {
  LocalPhoto({required this.kind, required this.clientId, required this.capturedAt, this.lat, this.lng, this.locationNote, this.synced = false, this.serverId});
  final String kind, clientId;
  final DateTime capturedAt;
  final double? lat, lng;
  final String? locationNote;
  bool synced;
  String? serverId;

  Map<String, dynamic> toJson() => {'kind': kind, 'clientId': clientId, 'capturedAt': capturedAt.toIso8601String(), 'lat': lat, 'lng': lng, 'locationNote': locationNote, 'synced': synced, 'serverId': serverId};
  static LocalPhoto fromJson(Map<String, dynamic> j) => LocalPhoto(
      kind: j['kind'] as String, clientId: j['clientId'] as String, capturedAt: DateTime.parse(j['capturedAt'] as String),
      lat: (j['lat'] as num?)?.toDouble(), lng: (j['lng'] as num?)?.toDouble(), locationNote: j['locationNote'] as String?,
      synced: j['synced'] as bool? ?? false, serverId: j['serverId'] as String?);
}

/// Everything the driver did for one trip's report, so the app works with or without a network.
class LocalReport {
  LocalReport(this.tripId);
  final String tripId;
  String? reportId;
  Map<String, dynamic>? server;          // last detail returned by the server
  Map<String, dynamic>? header;          // trip summary from «mine» (before the report exists on the server)
  final photos = <String, LocalPhoto>{}; // latest photo per slot
  final inputs = <String, dynamic>{};    // loadedQty, meterSerial, meterBefore, meterAfter, tankState, outcome, outcomeReason
  final stagesDone = <String>{};         // confirmed locally (sent or waiting)
  bool submitted = false;                // driver pressed «إرسال»
  String? lastError;                     // the server's refusal, shown as is

  String get status => server?['status'] as String? ?? 'Draft';
  bool get locked => status == 'Submitted' || status == 'NeedsReview' || status == 'Approved';

  Map<String, dynamic> toJson() => {
        'tripId': tripId, 'reportId': reportId, 'server': server, 'header': header, 'photos': photos.values.map((p) => p.toJson()).toList(),
        'inputs': inputs, 'stagesDone': stagesDone.toList(), 'submitted': submitted, 'lastError': lastError,
      };
  static LocalReport fromJson(Map<String, dynamic> j) {
    final r = LocalReport(j['tripId'] as String)
      ..reportId = j['reportId'] as String?
      ..server = (j['server'] as Map?)?.cast<String, dynamic>()
      ..header = (j['header'] as Map?)?.cast<String, dynamic>()
      ..submitted = j['submitted'] as bool? ?? false
      ..lastError = j['lastError'] as String?;
    for (final p in (j['photos'] as List? ?? [])) {
      final lp = LocalPhoto.fromJson((p as Map).cast<String, dynamic>());
      r.photos[lp.kind] = lp;
    }
    r.inputs.addAll((j['inputs'] as Map? ?? {}).cast<String, dynamic>());
    r.stagesDone.addAll((j['stagesDone'] as List? ?? []).cast<String>());
    return r;
  }
}

/// A step waiting to reach the server, in the order the driver did it.
class Job {
  Job(this.type, this.tripId, {this.kind, this.clientId, this.stage, this.body, DateTime? at}) : at = at ?? DateTime.now();
  final String type; // start | photo | stage | submit
  final String tripId;
  final String? kind, clientId, stage;
  final Map<String, dynamic>? body;
  final DateTime at;
  Map<String, dynamic> toJson() => {'type': type, 'tripId': tripId, 'kind': kind, 'clientId': clientId, 'stage': stage, 'body': body, 'at': at.toIso8601String()};
  static Job fromJson(Map<String, dynamic> j) => Job(j['type'] as String, j['tripId'] as String,
      kind: j['kind'] as String?, clientId: j['clientId'] as String?, stage: j['stage'] as String?, body: (j['body'] as Map?)?.cast<String, dynamic>(), at: DateTime.parse(j['at'] as String));
}

/// Device storage. Phones: the app's private documents folder (survives closing the app and losing the network).
/// Browser build: memory only, so the driver is told not to close the page before syncing.
class DeviceStore {
  static final _mem = <String, Uint8List>{};
  static Directory? _dir;
  static bool get persistent => !kIsWeb;

  static Future<Directory?> _root() async {
    if (kIsWeb) return null;
    if (_dir != null) return _dir;
    try {
      final d = Directory('${(await getApplicationDocumentsDirectory()).path}/fill_reports');
      await d.create(recursive: true);
      return _dir = d;
    } catch (_) {
      return null; // no plugin (tests) → memory
    }
  }

  static Future<void> write(String name, Uint8List bytes) async {
    final d = await _root();
    if (d == null) {
      _mem[name] = bytes;
    } else {
      await File('${d.path}/$name').writeAsBytes(bytes, flush: true);
    }
  }

  static Future<Uint8List?> read(String name) async {
    final d = await _root();
    if (d == null) return _mem[name];
    final f = File('${d.path}/$name');
    return await f.exists() ? f.readAsBytes() : null;
  }

  static Future<void> delete(String name) async {
    final d = await _root();
    if (d == null) {
      _mem.remove(name);
    } else {
      final f = File('${d.path}/$name');
      if (await f.exists()) await f.delete();
    }
  }

  /// For tests: forget everything kept in memory.
  @visibleForTesting
  static void resetMemory() => _mem.clear();
}

bool isNetworkError(Object e) => e is! ApiException || e.status == 429 || e.status >= 500 || e.status == 0;

/// Keeps the driver's work and sends it in order. Every call is safe to repeat:
/// «start» returns the existing report, photos carry a clientId, stages overwrite, «submit» is idempotent.
class FillOutbox extends ChangeNotifier {
  FillOutbox._();
  static final FillOutbox instance = FillOutbox._();

  final reports = <String, LocalReport>{};
  final jobs = <Job>[];
  bool online = true;
  bool syncing = false;
  bool _loaded = false;
  Timer? _timer;
  /// Retries in the background every 20 s. Tests turn it off (no stray timers).
  static bool autoSync = true;

  int pendingFor(String tripId) => jobs.where((j) => j.tripId == tripId).length;
  int get pending => jobs.length;

  Future<void> load() async {
    if (_loaded) return;
    _loaded = true;
    final raw = await DeviceStore.read('state.json');
    if (raw != null) {
      try {
        final j = jsonDecode(utf8.decode(raw)) as Map<String, dynamic>;
        for (final r in (j['reports'] as List? ?? [])) {
          final lr = LocalReport.fromJson((r as Map).cast<String, dynamic>());
          reports[lr.tripId] = lr;
        }
        jobs.addAll((j['jobs'] as List? ?? []).map((x) => Job.fromJson((x as Map).cast<String, dynamic>())));
      } catch (_) {/* damaged file: start clean, server still has what was synced */}
    }
    if (autoSync) _timer ??= Timer.periodic(const Duration(seconds: 20), (_) { if (jobs.isNotEmpty) sync(); });
  }

  Future<void> _save() async {
    notifyListeners();
    final j = {'reports': reports.values.map((r) => r.toJson()).toList(), 'jobs': jobs.map((x) => x.toJson()).toList()};
    await DeviceStore.write('state.json', Uint8List.fromList(utf8.encode(jsonEncode(j))));
  }

  /// For tests / sign-out: drop all local state.
  Future<void> clear() async {
    reports.clear();
    jobs.clear();
    await DeviceStore.delete('state.json');
    notifyListeners();
  }

  LocalReport report(String tripId, {Map<String, dynamic>? header}) {
    final r = reports.putIfAbsent(tripId, () => LocalReport(tripId));
    if (header != null) r.header = header;
    return r;
  }

  /// «ابدأ التوثيق»: works offline; the server report is created at the first sync.
  Future<LocalReport> start(String tripId, Map<String, dynamic> header) async {
    final r = report(tripId, header: header);
    if (r.reportId == null && !jobs.any((j) => j.tripId == tripId && j.type == 'start')) jobs.add(Job('start', tripId));
    await _save();
    await sync();
    return r;
  }

  Future<void> addPhoto(String tripId, Uint8List bytes, {required String kind, double? lat, double? lng, String? locationNote}) async {
    final r = report(tripId);
    final old = r.photos[kind];
    final p = LocalPhoto(kind: kind, clientId: newClientId(), capturedAt: DateTime.now(), lat: lat, lng: lng, locationNote: locationNote);
    await DeviceStore.write('${p.clientId}.jpg', bytes);
    // a retake replaces a photo that never left the phone; one already on the server stays there as history
    if (old != null && !old.synced) {
      jobs.removeWhere((j) => j.type == 'photo' && j.clientId == old.clientId);
      await DeviceStore.delete('${old.clientId}.jpg');
    }
    r.photos[kind] = p;
    r.lastError = null;
    jobs.add(Job('photo', tripId, kind: kind, clientId: p.clientId));
    await _save();
    unawaited(sync());
  }

  Future<void> confirmStage(String tripId, String stage, Map<String, dynamic> body) async {
    final r = report(tripId);
    r.inputs.addAll(body);
    r.stagesDone.add(stage);
    r.lastError = null;
    jobs.removeWhere((j) => j.tripId == tripId && j.type == 'stage' && j.stage == stage);
    jobs.add(Job('stage', tripId, stage: stage, body: {...body, 'at': DateTime.now().toUtc().toIso8601String()}));
    await _save();
    unawaited(sync());
  }

  Future<void> submit(String tripId) async {
    final r = report(tripId);
    r.submitted = true;
    r.lastError = null;
    if (!jobs.any((j) => j.tripId == tripId && j.type == 'submit')) jobs.add(Job('submit', tripId));
    await _save();
    await sync();
  }

  /// Latest server state of a report (when online). Keeps local work that has not been sent yet.
  Future<void> refresh(String tripId) async {
    final r = report(tripId);
    if (r.reportId == null) return;
    try {
      r.server = (await Api.instance.get('/api/fill-reports/${r.reportId}') as Map).cast<String, dynamic>();
      online = true;
      _absorb(r);
      await _save();
    } catch (e) {
      if (isNetworkError(e)) online = false;
      notifyListeners();
    }
  }

  void _absorb(LocalReport r) {
    final s = r.server;
    if (s == null) return;
    final st = (s['stages'] as Map).cast<String, dynamic>();
    const keys = {'pre-fill': 'preFill', 'post-fill': 'postFill', 'arrival': 'arrival', 'post-unload': 'postUnload'};
    final waiting = jobs.where((j) => j.tripId == r.tripId && j.type == 'stage').map((j) => j.stage).toSet();
    for (final e in keys.entries) {
      final done = (st[e.value] as Map)['done'] == true;
      if (done) r.stagesDone.add(e.key);
      if (!done && !waiting.contains(e.key)) r.stagesDone.remove(e.key);
    }
    final pf = (st['postFill'] as Map).cast<String, dynamic>(), ar = (st['arrival'] as Map).cast<String, dynamic>(), pu = (st['postUnload'] as Map).cast<String, dynamic>();
    void fill(String k, Object? v) { if (v != null && !r.inputs.containsKey(k)) r.inputs[k] = v; }
    fill('loadedQty', pf['loadedQty']);
    fill('meterSerial', ar['secretMeterSerial']);
    fill('meterBefore', ar['meterBefore']);
    fill('meterAfter', pu['meterAfter']);
    fill('tankState', pu['tankState']);
    if (r.status == 'Returned') r.submitted = jobs.any((j) => j.tripId == r.tripId && j.type == 'submit');
  }

  /// Sends waiting steps in order. Stops at the first network problem and tries again later.
  Future<void>? _running;

  /// Callers share one run; a caller that arrives mid-run waits for it, then runs once more if work is left.
  Future<void> sync() async {
    if (_running != null) {
      await _running;
      if (jobs.isEmpty || !online) return;
    }
    await (_running ??= _sync().whenComplete(() => _running = null));
  }

  Future<void> _sync() async {
    syncing = true;
    notifyListeners();
    try {
      while (jobs.isNotEmpty) {
        final j = jobs.first;
        final r = report(j.tripId);
        try {
          await _run(j, r);
          jobs.removeAt(0);
          online = true;
        } catch (e) {
          if (isNetworkError(e)) {
            online = false;
            break;
          }
          // a business-rule refusal: show it, drop this step and what depends on it; the driver fixes and confirms again
          r.lastError = (e as ApiException).message;
          if (j.type == 'stage') r.stagesDone.remove(j.stage);
          if (j.type == 'submit' || j.type == 'stage') r.submitted = false;
          jobs.removeAt(0);
          if (j.type != 'photo') jobs.removeWhere((x) => x.tripId == j.tripId && (x.type == 'stage' || x.type == 'submit'));
          if (e.status == 404 || e.status == 403) jobs.removeWhere((x) => x.tripId == j.tripId);
        }
        await _save();
      }
    } finally {
      syncing = false;
      await _save();
    }
  }

  Future<void> _run(Job j, LocalReport r) async {
    final api = Api.instance;
    if (r.reportId == null) {
      final d = (await api.post('/api/fill-reports/start', {'tripId': j.tripId}) as Map).cast<String, dynamic>();
      r.reportId = d['id'] as String;
      r.server = d;
      _absorb(r);
    }
    final id = r.reportId!;
    switch (j.type) {
      case 'start':
        return;
      case 'photo':
        final p = r.photos.values.firstWhere((x) => x.clientId == j.clientId, orElse: () => LocalPhoto(kind: j.kind!, clientId: j.clientId!, capturedAt: j.at));
        final bytes = await DeviceStore.read('${j.clientId}.jpg');
        if (bytes == null) return; // replaced by a retake before it was sent
        final res = await api.uploadFillPhoto(id, bytes, kind: p.kind, clientId: p.clientId, capturedAt: p.capturedAt, lat: p.lat, lng: p.lng, locationNote: p.locationNote);
        p.synced = true;
        p.serverId = res['id'] as String?;
        return;
      case 'stage':
        r.server = (await api.post('/api/fill-reports/$id/stages/${j.stage}', j.body) as Map).cast<String, dynamic>();
        return;
      case 'submit':
        r.server = (await api.post('/api/fill-reports/$id/submit') as Map).cast<String, dynamic>();
        r.lastError = null;
        // the report is on the server: free the phone's copies of the photos
        for (final p in r.photos.values.where((p) => p.synced)) {
          await DeviceStore.delete('${p.clientId}.jpg');
        }
        return;
    }
  }
}
