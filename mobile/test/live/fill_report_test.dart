// Drives the real driver app against a running FuelUp server: «تقرير التعبئة» from «ابدأ التوثيق» to «تم إرسال التقرير»,
// including a stretch with no network (photos and a stage kept on the phone, then synced once), a missing mandatory photo,
// a retake, a photo with no location (reason chosen), and a partial unload that goes to review.
// Only the camera, GPS and the key store are simulated.
//
//   cd tests && node fill-app-flow.mjs prepare
//   cd mobile && flutter test test/live/fill_report_test.dart --dart-define=API_URL=http://127.0.0.1:5004 <DEFINES printed above>
//   cd tests && node fill-app-flow.mjs verify
import 'dart:io';
import 'dart:math';

import 'package:flutter/material.dart';
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

const _jpeg = <int>[255,216,255,224,0,16,74,70,73,70,0,1,1,0,0,1,0,1,0,0,255,219,0,67,0,10,7,7,8,7,6,10,8,8,8,11,10,10,11,14,24,16,14,13,13,14,29,21,22,17,24,35,31,37,36,34,31,34,33,38,43,55,47,38,41,52,41,33,34,48,65,49,52,57,59,62,62,62,37,46,68,73,67,60,72,55,61,62,59,255,219,0,67,1,10,11,11,14,13,14,28,16,16,28,59,40,34,40,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,59,255,192,0,17,8,0,12,0,16,3,1,34,0,2,17,1,3,17,1,255,196,0,31,0,0,1,5,1,1,1,1,1,1,0,0,0,0,0,0,0,0,1,2,3,4,5,6,7,8,9,10,11,255,196,0,181,16,0,2,1,3,3,2,4,3,5,5,4,4,0,0,1,125,1,2,3,0,4,17,5,18,33,49,65,6,19,81,97,7,34,113,20,50,129,145,161,8,35,66,177,193,21,82,209,240,36,51,98,114,130,9,10,22,23,24,25,26,37,38,39,40,41,42,52,53,54,55,56,57,58,67,68,69,70,71,72,73,74,83,84,85,86,87,88,89,90,99,100,101,102,103,104,105,106,115,116,117,118,119,120,121,122,131,132,133,134,135,136,137,138,146,147,148,149,150,151,152,153,154,162,163,164,165,166,167,168,169,170,178,179,180,181,182,183,184,185,186,194,195,196,197,198,199,200,201,202,210,211,212,213,214,215,216,217,218,225,226,227,228,229,230,231,232,233,234,241,242,243,244,245,246,247,248,249,250,255,196,0,31,1,0,3,1,1,1,1,1,1,1,1,1,0,0,0,0,0,0,1,2,3,4,5,6,7,8,9,10,11,255,196,0,181,17,0,2,1,2,4,4,3,4,7,5,4,4,0,1,2,119,0,1,2,3,17,4,5,33,49,6,18,65,81,7,97,113,19,34,50,129,8,20,66,145,161,177,193,9,35,51,82,240,21,98,114,209,10,22,36,52,225,37,241,23,24,25,26,38,39,40,41,42,53,54,55,56,57,58,67,68,69,70,71,72,73,74,83,84,85,86,87,88,89,90,99,100,101,102,103,104,105,106,115,116,117,118,119,120,121,122,130,131,132,133,134,135,136,137,138,146,147,148,149,150,151,152,153,154,162,163,164,165,166,167,168,169,170,178,179,180,181,182,183,184,185,186,194,195,196,197,198,199,200,201,202,210,211,212,213,214,215,216,217,218,226,227,228,229,230,231,232,233,234,242,243,244,245,246,247,248,249,250,255,218,0,12,3,1,0,2,17,3,17,0,63,0,224,104,162,138,251,35,140,255,217];

class FakeCamera extends ImagePickerPlatform with MockPlatformInterfaceMixin {
  final _rnd = Random();
  int taken = 0;
  @override
  Future<XFile?> getImageFromSource({required ImageSource source, ImagePickerOptions options = const ImagePickerOptions()}) async {
    // a real (tiny) JPEG; a comment segment with random bytes makes every photo distinct
    final bytes = <int>[..._jpeg.sublist(0, 2), 0xFF, 0xFE, 0, 34, ...List.generate(32, (_) => _rnd.nextInt(256)), ..._jpeg.sublist(2)];
    final f = File('${Directory.systemTemp.path}/fr_photo_${DateTime.now().microsecondsSinceEpoch}.jpg');
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
  testWidgets('driver documents a fill report (partly offline) and it reaches operations', (tester) async {
    HttpOverrides.global = null;
    FlutterSecureStorage.setMockInitialValues({});
    FillOutbox.autoSync = false;
    final camera = FakeCamera();
    ImagePickerPlatform.instance = camera;
    var gps = true;
    FillLocation.current = () async => gps ? (lat: 24.7136, lng: 46.6753) : null;
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
    Future<void> photo(String label, {bool meter = false, bool retakeFirst = false}) async {
      await tapText('صوّر: $label');
      if (meter) { await settle(find.text('افتح الكاميرا')); await tapText('افتح الكاميرا'); }
      await settle(find.text('استخدم الصورة'));
      if (retakeFirst) { await tapText('إعادة التصوير'); await settle(find.text('استخدم الصورة')); }
      await tapText('استخدم الصورة');
      await idle(800);
    }

    await tester.pumpWidget(const FuelUpApp(signedIn: false));
    await type('اسم المستخدم', user);
    await type('كلمة المرور', pass);
    await tapText('تسجيل الدخول');

    // the main tab lists the assigned trip → «ابدأ التوثيق»
    await settle(find.textContaining(tripNo));
    expect(find.text('تقرير التعبئة'), findsWidgets);
    await tapText('ابدأ التوثيق');
    await settle(find.textContaining('FR-'));       // report created on the server and linked to the trip
    expect(find.textContaining('مقطورة'), findsOneWidget);

    debugPrint('✔ report started');
    // 1. before filling: trying to continue without the photo names exactly what is missing
    await tapText('تأكيد ومتابعة');
    expect(find.text('صوّر الصهريج قبل التعبئة'), findsOneWidget);
    await photo('الصهريج قبل التعبئة');
    await settle(find.textContaining('✓ أُرسلت'));
    await tapText('إعادة التصوير');                   // retake after it was sent: the first stays on the server as history
    await settle(find.text('استخدم الصورة'));
    await tapText('استخدم الصورة');
    await idle(1500);
    await tapText('تأكيد ومتابعة');
    await settle(find.text('الكمية المحمّلة (لتر)'));

    debugPrint('✔ stage 1');
    // 2. after filling — the network drops while photographing
    HttpOverrides.global = Offline();
    await photo('الصهريج بعد التعبئة', retakeFirst: true);
    await type('الكمية المحمّلة (لتر)', '19500');
    await tapText('تأكيد ومتابعة');
    await settle(find.textContaining('بانتظار المزامنة'));
    expect(find.textContaining('لا يوجد اتصال'), findsOneWidget);
    // 3. arrival — still offline: the work continues on the phone
    await photo('الصهريج ممتلئ عند العميل');
    await tapText('تأكيد ومتابعة');
    expect(find.text('صوّر قراءة العداد قبل التفريغ'), findsOneWidget);   // mandatory meter photo missing
    await photo('العداد السري قبل التفريغ', meter: true);
    await type('قراءة العداد قبل التفريغ', '100000');
    await tapText('تأكيد ومتابعة');
    await settle(find.text('نتيجة التفريغ'));
    expect(FillOutbox.instance.pending, greaterThanOrEqualTo(5));

    debugPrint('✔ offline stages 2-3');
    // network back → one sync, nothing duplicated
    HttpOverrides.global = null;
    await tapText('مزامنة الآن');
    final ob = FillOutbox.instance;
    for (var i = 0; i < 300 && (ob.pending > 0 || ob.syncing); i++) { await idle(100); }
    expect(ob.pending, 0);
    ob.sync();   // a second sync has nothing to send again
    for (var i = 0; i < 50 && ob.syncing; i++) { await idle(100); }

    debugPrint('✔ synced');
    // 4. after unloading: partial, with reason; one photo without GPS (reason chosen)
    await tapText('جزئي');
    await type('سبب التفريغ الجزئي', 'خزان العميل امتلأ قبل اكتمال التفريغ');
    await photo('الصهريج بعد التفريغ');
    gps = false;
    await tapText('صوّر: العداد السري بعد التفريغ');
    await settle(find.text('افتح الكاميرا')); await tapText('افتح الكاميرا');
    await settle(find.text('استخدم الصورة')); await tapText('استخدم الصورة');
    await settle(find.text('لا توجد إشارة GPS في المكان')); await tapText('لا توجد إشارة GPS في المكان');
    await idle(1500);
    await type('قراءة العداد بعد التفريغ', '112000');
    await tester.ensureVisible(find.text('حالة الصهريج بعد التفريغ'));
    await tester.tap(find.text('حالة الصهريج بعد التفريغ')); await idle(400);
    await tester.tap(find.text('متبقٍ فيه وقود').last); await idle(400);
    expect(find.textContaining('الكمية المسلّمة حسب العداد: 12,000'), findsOneWidget);
    await tapText('تأكيد ومتابعة');

    debugPrint('✔ stage 4');
    // review screen → send
    await settle(find.text('ملخص التقرير'));
    expect(find.text('12,000 لتر'), findsOneWidget);
    expect(find.text('-7,500 لتر'), findsOneWidget);
    await tapText('إرسال التقرير');
    await settle(find.textContaining('رقم التقرير: FR-'));
    expect(find.text('تم إرسال التقرير'), findsOneWidget);
    expect(find.textContaining('بحاجة إلى مراجعة'), findsWidgets);
    expect(camera.taken, 8);   // 6 slots + a retake after sending + a retake in the preview
    await tapText('تم');
    await idle(500);
  }, timeout: const Timeout(Duration(minutes: 8)), skip: user.isEmpty);
}
