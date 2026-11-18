// «توثيق التعبئة» from the real driver app, checked from the office side while the test runs (tests/fill-app-flow.mjs loading):
//   1. the pre-fill photo is sent online → the office sees the record «بانتظار صورة بعد التعبئة»;
//   2. the network drops: the post-fill photo, the loading document number and its photo are kept on the phone, then synced →
//      same record, «مكتمل», the photo keeps its capture time (earlier than its upload) and coordinates;
//   3. the report is sent; the supervisor asks to retake the post-fill photo → the app shows the note on that slot of the locked
//      report and the list badge «مطلوب إعادة تصوير»; the driver retakes it and the request is resolved.
// The test and the office script hand over through files in SYNC_DIR (stepN written by the app, goN answered by the office).
// Only the camera, GPS and the key store are simulated.
//
//   cd tests && API=http://127.0.0.1:5005 node fill-app-flow.mjs loading     (runs flutter test itself)
import 'dart:io';
import 'dart:math';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:fuelup_driver/fill/fill_screen.dart';
import 'package:fuelup_driver/fill/outbox.dart';
import 'package:fuelup_driver/main.dart';
import 'package:image_picker_platform_interface/image_picker_platform_interface.dart';
import 'package:plugin_platform_interface/plugin_platform_interface.dart';

const user = String.fromEnvironment('APP_USER');
const pass = String.fromEnvironment('APP_PASS');
const tripNo = String.fromEnvironment('TRIP_NO');
const photoPath = String.fromEnvironment('PHOTO');
const syncDir = String.fromEnvironment('SYNC_DIR');
const docNo = String.fromEnvironment('DOC_NO', defaultValue: 'ARM-LIVE-1');

class FakeCamera extends ImagePickerPlatform with MockPlatformInterfaceMixin {
  final _rnd = Random();
  int taken = 0;
  @override
  Future<XFile?> getImageFromSource({required ImageSource source, ImagePickerOptions options = const ImagePickerOptions()}) async {
    expect(source, ImageSource.camera);   // camera only, never the gallery
    final base = File(photoPath).readAsBytesSync();
    final bytes = <int>[...base.sublist(0, 2), 0xFF, 0xFE, 0, 34, ...List.generate(32, (_) => _rnd.nextInt(256)), ...base.sublist(2)];
    final f = File('${Directory.systemTemp.path}/ld_photo_${DateTime.now().microsecondsSinceEpoch}.jpg');
    f.writeAsBytesSync(bytes);
    taken++;
    return XFile(f.path);
  }
}

/// No network: every connection goes to a closed port.
class Offline extends HttpOverrides {
  @override
  HttpClient createHttpClient(SecurityContext? c) => super.createHttpClient(c)..findProxy = (_) => 'PROXY 127.0.0.1:9';
}

void main() {
  testWidgets('loading documentation: pre-fill online, post-fill offline, retake on a sent report', (tester) async {
    HttpOverrides.global = null;
    FlutterSecureStorage.setMockInitialValues({});
    FillOutbox.autoSync = false;
    final camera = FakeCamera();
    ImagePickerPlatform.instance = camera;
    FillLocation.current = () async => (lat: 24.7136, lng: 46.6753);
    tester.view.physicalSize = const Size(1080, 6000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    Future<void> settle(Finder f, {int seconds = 40}) async {
      for (var i = 0; i < seconds * 10; i++) {
        await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));
        await tester.pump(const Duration(milliseconds: 100));
        if (f.evaluate().isNotEmpty) { for (var k = 0; k < 6; k++) { await tester.pump(const Duration(milliseconds: 100)); } return; }
      }
      final shown = find.byType(RichText, skipOffstage: false).evaluate().map((e) => (e.widget as RichText).text.toPlainText()).where((x) => x.trim().isNotEmpty).join(' | ');
      fail('timed out waiting for $f. On screen: $shown');
    }

    Future<void> idle([int ms = 600]) async { for (var i = 0; i < ms ~/ 100; i++) { await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100))); await tester.pump(const Duration(milliseconds: 100)); } }
    Future<void> tapText(String t) async { await tester.ensureVisible(find.text(t).last); await tester.tap(find.text(t).last); await idle(300); }
    Future<void> type(String label, String v) async { final f = find.widgetWithText(TextField, label); await tester.ensureVisible(f); await tester.enterText(f, v); await tester.pump(); }
    Future<void> photo(String label) async {
      await tapText('صوّر: $label');
      await settle(find.text('استخدم الصورة'));
      await tapText('استخدم الصورة');
      await idle(800);
    }
    final ob = FillOutbox.instance;
    Future<void> drain() async { for (var i = 0; i < 300 && (ob.pending > 0 || ob.syncing); i++) { await idle(100); } expect(ob.pending, 0); }
    /// Hands over to the office script and waits for its answer («ok» or the failure it found).
    Future<void> handover(int n, [String body = '']) async {
      File('$syncDir/step$n').writeAsStringSync(body);
      final go = File('$syncDir/go$n');
      await tester.runAsync(() async { for (var i = 0; i < 600 && !go.existsSync(); i++) { await Future<void>.delayed(const Duration(milliseconds: 200)); } });
      expect(go.existsSync() ? go.readAsStringSync() : 'office did not answer step $n', 'ok');
      debugPrint('✔ office check $n');
    }

    await tester.runAsync(() async {
      final f = FontLoader('Tajawal');
      for (final w in ['Regular', 'Bold', 'ExtraBold']) {
        f.addFont(rootBundle.load('assets/fonts/Tajawal-$w.ttf'));
      }
      await f.load();
    });
    await tester.pumpWidget(const FuelUpApp(signedIn: false));
    await type('اسم المستخدم', user);
    await type('كلمة المرور', pass);
    await tapText('تسجيل الدخول');
    await settle(find.textContaining(tripNo));
    await tapText('ابدأ التوثيق');
    await settle(find.textContaining('FR-'));

    // 1. pre-fill photo, online → the record appears in «توثيق التعبئة»
    await photo('الصهريج قبل التعبئة');
    await settle(find.textContaining('✓ أُرسلت'));
    await handover(1);
    await tapText('تأكيد ومتابعة');
    await settle(find.text('الكمية المحمّلة (لتر)'));

    // 2. post-fill offline: photo, quantity, loading document no. and its photo stay on the phone
    HttpOverrides.global = Offline();
    expect(find.text('رقم سند التحميل / فاتورة المحطة (اختياري)'), findsOneWidget);
    await photo('الصهريج بعد التعبئة');
    final takenAt = ob.reports.values.firstWhere((r) => r.photos.containsKey('tank-post-fill')).photos['tank-post-fill']!.capturedAt;
    await photo('سند التحميل');
    await type('الكمية المحمّلة (لتر)', '19800');
    await type('رقم سند التحميل / فاتورة المحطة (اختياري)', docNo);
    await tapText('تأكيد ومتابعة');
    await settle(find.textContaining('بانتظار المزامنة'));
    expect(find.textContaining('لا يوجد اتصال'), findsOneWidget);
    await idle(8000);   // still offline for a while: the upload time must come after the capture time
    expect(ob.pending, greaterThanOrEqualTo(3));
    HttpOverrides.global = null;
    await tapText('مزامنة الآن');
    await drain();
    await handover(2, takenAt.toUtc().toIso8601String());

    // finish the trip quickly and send the report (it becomes locked)
    await photo('الصهريج ممتلئ عند العميل');
    await tapText('صوّر: العداد السري قبل التفريغ');
    await settle(find.text('افتح الكاميرا')); await tapText('افتح الكاميرا');
    await settle(find.text('استخدم الصورة')); await tapText('استخدم الصورة'); await idle(800);
    await type('قراءة العداد قبل التفريغ', '200000');
    await tapText('تأكيد ومتابعة');
    await settle(find.text('نتيجة التفريغ'));
    await photo('الصهريج بعد التفريغ');
    await tapText('صوّر: العداد السري بعد التفريغ');
    await settle(find.text('افتح الكاميرا')); await tapText('افتح الكاميرا');
    await settle(find.text('استخدم الصورة')); await tapText('استخدم الصورة'); await idle(800);
    await type('قراءة العداد بعد التفريغ', '219800');
    await tester.ensureVisible(find.text('حالة الصهريج بعد التفريغ'));
    await tester.tap(find.text('حالة الصهريج بعد التفريغ')); await idle(400);
    await tester.tap(find.text('فارغ').last); await idle(400);
    await tapText('تأكيد ومتابعة');
    await settle(find.text('ملخص التقرير'));
    await tapText('إرسال التقرير');
    await settle(find.textContaining('رقم التقرير: FR-'));
    await tapText('تم');
    await drain();

    // 3. the supervisor asks for the post-fill photo again; the driver sees it in the list and on the slot of the locked report
    await handover(3);
    await tester.binding.handlePopRoute();   // the phone's back button: back to the list, which reloads from the server
    await settle(find.text('مطلوب إعادة تصوير'));
    expect(find.textContaining('أعد تصوير الصهريج بعد التعبئة'), findsWidgets);
    await tapText('إعادة التصوير المطلوبة');
    await settle(find.textContaining('مطلوب إعادة التصوير: '));
    expect(find.textContaining('طلب المشرف إعادة تصوير «الصهريج بعد التعبئة»'), findsOneWidget);
    expect(find.text('إرسال التقرير'), findsNothing);   // the rest of the report stays locked
    await tapText('إعادة التصوير');
    await settle(find.text('استخدم الصورة'));
    await tapText('استخدم الصورة');
    await drain();
    for (var i = 0; i < 100 && find.textContaining('مطلوب إعادة التصوير: ').evaluate().isNotEmpty; i++) { await idle(200); }
    expect(find.textContaining('مطلوب إعادة التصوير: '), findsNothing);   // resolved on the server: the slot is locked again
    await handover(4);
    expect(camera.taken, 8);   // 6 slots + the loading document + the requested retake
  }, timeout: const Timeout(Duration(minutes: 10)), skip: user.isEmpty);
}
