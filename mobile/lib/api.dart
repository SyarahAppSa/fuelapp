import 'dart:convert';

import 'package:image_picker/image_picker.dart' show XFile;

import 'package:http/http.dart' as http;
import 'package:http_parser/http_parser.dart';
import 'package:intl/intl.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Base URL of the central API. Override at build time:
/// flutter build apk --release --dart-define=API_URL=https://api.example.com
/// No secret of any kind is compiled into the app: only this public address.
const _configuredBase = String.fromEnvironment('API_URL', defaultValue: 'http://10.0.2.2:5004');

/// Test (staging) builds let the tester type the server address on the sign-in screen:
/// flutter build apk --release --dart-define=ALLOW_SERVER_CHANGE=true
/// Production builds keep the address fixed at build time.
const allowServerChange = bool.fromEnvironment('ALLOW_SERVER_CHANGE');
String? _serverOverride;

/// Release builds refuse to talk to anything but HTTPS (tokens never travel in clear text).
String get apiBase {
  final base = _serverOverride ?? _configuredBase;
  // An empty base means "same site" (browser build served by the FuelUp web server itself).
  if (kReleaseMode && base.isNotEmpty && !base.startsWith('https://')) {
    throw StateError('Release builds require an https:// server address');
  }
  return base;
}

String get serverAddress => _serverOverride ?? _configuredBase;

/// Validates and stores the server address chosen on a test build. Returns an Arabic error, or null.
Future<String?> setServerAddress(String input) async {
  if (!allowServerChange) return 'تغيير الخادم غير متاح في هذه النسخة.';
  var url = input.trim();
  while (url.endsWith('/')) {
    url = url.substring(0, url.length - 1);
  }
  final uri = Uri.tryParse(url);
  if (uri == null || !uri.hasAuthority || !(uri.scheme == 'https' || (!kReleaseMode && uri.scheme == 'http'))) {
    return 'أدخل عنوانًا يبدأ بـ https://';
  }
  _serverOverride = url;
  await _store.write(key: 'server', value: url);
  return null;
}

/// Tokens are kept in the platform's secure store: iOS Keychain (this device only, after first unlock)
/// and Android Keystore-backed encryption. Never in plain preferences, files or logs.
const _store = FlutterSecureStorage(
  iOptions: IOSOptions(accessibility: KeychainAccessibility.first_unlock_this_device),
);

/// Error raised by the server (business-rule rejection, permission, etc.).
/// [message] is the Arabic text returned by the API and is shown to the driver as is.
class ApiException implements Exception {
  ApiException(this.status, this.message);
  final int status;
  final String message;
  @override
  String toString() => message;
}

class MfaChallenge {
  MfaChallenge(this.token);
  final String token;
}

class Api {
  Api._();
  static final Api instance = Api._();

  String? _access;
  String? _refresh;

  Future<bool> restore() async {
    try {
      if (allowServerChange) _serverOverride = await _store.read(key: 'server');
      _refresh = await _store.read(key: 'refresh');
    } catch (_) {
      await _store.deleteAll();   // unreadable store (e.g. restored onto another device): start clean
      _refresh = null;
    }
    _access = null;               // short-lived: obtained again with the refresh token
    return _refresh != null;
  }

  Future<void> _save(Map<String, dynamic> t) async {
    _access = t['accessToken'] as String;
    _refresh = t['refreshToken'] as String;
    await _store.write(key: 'refresh', value: _refresh);
  }

  /// Returns null when signed in, or an MFA ticket when the account needs a verification code.
  Future<MfaChallenge?> login(String user, String pass) async {
    final r = await http.post(Uri.parse('$apiBase/api/auth/login'),
        headers: {'Content-Type': 'application/json'}, body: jsonEncode({'username': user, 'password': pass, 'client': 'mobile'}));
    final body = _decode(r) as Map<String, dynamic>;
    if (body['mfaSetupRequired'] == true) {
      throw ApiException(403, 'هذا الحساب يتطلب تفعيل التحقق بخطوتين. فعّله أولًا من لوحة الويب.');
    }
    if (body['mfaRequired'] == true) return MfaChallenge(body['mfaToken'] as String);
    await _save(body);
    return null;
  }

  Future<void> verifyMfa(MfaChallenge c, String code) async {
    final r = await http.post(Uri.parse('$apiBase/api/auth/mfa'),
        headers: {'Content-Type': 'application/json'}, body: jsonEncode({'mfaToken': c.token, 'code': code, 'client': 'mobile'}));
    await _save(_decode(r) as Map<String, dynamic>);
  }

  /// Ends the session on the server too, so a copied token is useless afterwards.
  Future<void> logout() async {
    try {
      if (_access != null || await _tryRefresh()) await post('/api/auth/logout');
    } catch (_) {}
    _access = _refresh = null;
    await _clearTokens();
  }

  /// Removes the tokens but keeps the chosen test server address.
  Future<void> _clearTokens() async {
    await _store.delete(key: 'refresh');
  }

  Future<bool> _tryRefresh() async {
    if (_refresh == null) return false;
    final r = await http.post(Uri.parse('$apiBase/api/auth/refresh'),
        headers: {'Content-Type': 'application/json'}, body: jsonEncode({'refreshToken': _refresh}));
    if (r.statusCode != 200) {
      _access = _refresh = null;
      await _clearTokens();
      return false;
    }
    await _save(jsonDecode(r.body) as Map<String, dynamic>);
    return true;
  }

  Map<String, String> get _headers => {'Content-Type': 'application/json', if (_access != null) 'Authorization': 'Bearer $_access'};

  Future<dynamic> _send(Future<http.Response> Function() call) async {
    if (_access == null) await _tryRefresh();
    var r = await call();
    if (r.statusCode == 401 && await _tryRefresh()) r = await call();
    return _decode(r);
  }

  dynamic _decode(http.Response r) {
    final body = r.body.isEmpty ? null : jsonDecode(utf8.decode(r.bodyBytes));
    if (r.statusCode >= 400) {
      final msg = body is Map && body['error'] != null ? body['error'] as String : 'تعذر تنفيذ الطلب (${r.statusCode})';
      throw ApiException(r.statusCode, msg);
    }
    return body;
  }

  Future<dynamic> get(String path) => _send(() => http.get(Uri.parse('$apiBase$path'), headers: _headers));
  Future<dynamic> post(String path, [Object? body]) =>
      _send(() => http.post(Uri.parse('$apiBase$path'), headers: _headers, body: jsonEncode(body ?? {})));

  /// Uploads a camera photo attached to an operation. The capture time is sent so the
  /// server can flag pictures picked from the gallery long after they were taken.
  Future<Map<String, dynamic>> upload(XFile file, {required String ownerType, required String ownerId, required String kind, DateTime? capturedAt}) async {
    Future<http.Response> call() async {
      final req = http.MultipartRequest('POST', Uri.parse('$apiBase/api/attachments'))
        ..headers['Authorization'] = 'Bearer $_access'
        ..fields['ownerType'] = ownerType
        ..fields['ownerId'] = ownerId
        ..fields['kind'] = kind
        ..fields['capturedAt'] = (capturedAt ?? DateTime.now()).toUtc().toIso8601String()
        ..files.add(http.MultipartFile.fromBytes('file', await file.readAsBytes(), filename: 'photo.jpg', contentType: MediaType('image', 'jpeg')));
      return http.Response.fromStream(await req.send());
    }

    return await _send(call) as Map<String, dynamic>;
  }
}

// ── Models (only the fields the driver app uses) ──────────────────────────────

class Vehicle {
  Vehicle(this.json);
  final Map<String, dynamic> json;
  String get id => json['id'] as String;
  String get plate => json['plate'] as String? ?? '';
  String get model => [json['make'], json['model'], json['year']].where((x) => x != null).join(' ');
  num get capacity => json['capacity'] as num? ?? 0;
}

class Me {
  Me(this.json);
  final Map<String, dynamic> json;
  String get name => json['displayName'] as String? ?? '';
  String? get driverId => json['driverId'] as String?;
  List<Vehicle> get vehicles => ((json['vehicles'] as List?) ?? []).map((v) => Vehicle(v as Map<String, dynamic>)).toList();
}

const openStatuses = {'WaitingLoad', 'Loaded', 'EnRoute', 'Arrived', 'Unloading'};

class TripView {
  TripView(this.json);
  final Map<String, dynamic> json;
  Map<String, dynamic> get trip => json['trip'] as Map<String, dynamic>;
  Map<String, dynamic> get rec => json['reconciliation'] as Map<String, dynamic>;
  Map<String, dynamic> get snap => (trip['snapshot'] as Map<String, dynamic>?) ?? {};
  String get id => trip['id'] as String;
  String get no => trip['no'] as String;
  String get status => trip['status'] as String;
  String get label => json['statusLabel'] as String;
  bool get isOpen => openStatuses.contains(status);
  String get customer => snap['customerName'] as String? ?? '';
  String get site => snap['site'] as String? ?? '';
  String get station => snap['stationName'] as String? ?? '';
  String? get vehicleId => trip['vehicleId'] as String?;
  num get qtyRequested => trip['qtyRequested'] as num? ?? 0;
  String? get meterSerial => snap['meterSerial'] as String?;
  List<Map<String, dynamic>> get handovers => ((trip['handovers'] as List?) ?? []).cast<Map<String, dynamic>>();
}

final _n = NumberFormat('#,##0.##', 'en');
String fmt(num? v) => v == null ? '—' : _n.format(v);
num? parseNum(String s) => num.tryParse(s.replaceAll(',', '').trim());
