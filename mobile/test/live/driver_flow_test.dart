// Drives the real driver app UI against a running FuelUp server (no mocked API):
// sign-in → my orders → load (with a meter reading the server must reject, then a valid one) → depart → arrive →
// seals → deliver with photos. Only the camera and the device key store are simulated (no device in CI).
//
//   cd tests && node app-flow.mjs prepare        (prints the --dart-define values)
//   cd mobile && flutter test test/live --dart-define=API_URL=http://127.0.0.1:5004 --dart-define=APP_USER=... --dart-define=APP_PASS=... --dart-define=TRIP_NO=...
//   cd tests && node app-flow.mjs verify
import 'dart:io';
import 'dart:math';

import 'package:flutter/material.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:fuelup_driver/fill/outbox.dart';
import 'package:fuelup_driver/main.dart';
import 'package:image_picker_platform_interface/image_picker_platform_interface.dart';
import 'package:plugin_platform_interface/plugin_platform_interface.dart';

const user = String.fromEnvironment('APP_USER');
const pass = String.fromEnvironment('APP_PASS');
const tripNo = String.fromEnvironment('TRIP_NO');

/// Stands in for the phone camera: every "photo" is a distinct, valid JPEG file.
class FakeCamera extends ImagePickerPlatform with MockPlatformInterfaceMixin {
  final _rnd = Random();
  int taken = 0;
  @override
  Future<XFile?> getImageFromSource({required ImageSource source, ImagePickerOptions options = const ImagePickerOptions()}) async {
    final bytes = <int>[0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1, ...List.generate(600, (_) => _rnd.nextInt(256)), 0xFF, 0xD9];
    final f = File('${Directory.systemTemp.path}/fuelup_photo_${DateTime.now().microsecondsSinceEpoch}.jpg');
    f.writeAsBytesSync(bytes);   // synchronous: completes inside the test clock
    taken++;
    return XFile(f.path);
  }
}

void main() {
  testWidgets('driver completes an order in the app and the server records it', (tester) async {
    HttpOverrides.global = null;                       // real network to the FuelUp server
    FlutterSecureStorage.setMockInitialValues({});     // device key store (Keychain/Keystore) simulated
    FillOutbox.autoSync = false;   // no background timer in a widget test
    final camera = FakeCamera();
    ImagePickerPlatform.instance = camera;
    tester.view.physicalSize = const Size(1080, 5200);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    Future<void> settle(Finder f, {int seconds = 30}) async {
      for (var i = 0; i < seconds * 10; i++) {
        await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));   // real time: network replies arrive
        await tester.pump(const Duration(milliseconds: 100));                                     // app time: animations advance
        if (f.evaluate().isNotEmpty) { for (var k = 0; k < 6; k++) { await tester.pump(const Duration(milliseconds: 100)); } return; }   // let page transitions finish (spinners never "settle")
      }
      final shown = find.byType(RichText, skipOffstage: false).evaluate().map((e) => (e.widget as RichText).text.toPlainText()).where((x) => x.trim().isNotEmpty).join(' | ');
      fail('timed out waiting for $f. On screen: $shown');
    }

    Finder field(String label) => find.widgetWithText(TextField, label);
    Future<void> type(String label, String v) async => tester.enterText(field(label), v);
    Future<void> tapText(String t) async { await tester.tap(find.text(t).last); await tester.pump(const Duration(milliseconds: 50)); }
    Future<void> photo(String label) async { await tester.tap(find.text('صورة $label')); await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 200))); await tester.pump(); }

    await tester.pumpWidget(const FuelUpApp(signedIn: false));
    await type('اسم المستخدم', user);
    await type('كلمة المرور', pass);
    await tapText('تسجيل الدخول');

    // Home → "my orders" tab lists the order the office just created
    await settle(find.text('طلباتي'));
    await tapText('طلباتي');
    await settle(find.textContaining(tripNo));
    await tester.tap(find.textContaining(tripNo));
    await settle(find.text('تأكيد التحميل'));

    // Loading: photos + a meter reading below the last approved one (50,000) → the server's rejection is shown as is
    await type('الكمية المحمّلة (لتر)', '4000');
    await type('رقم سند التحميل', 'APP-DOC-1');
    await type('قراءة عداد الكيلومترات', '120500');
    await type('قراءة العداد قبل التعبئة', '49000');
    await type('قراءة العداد بعد التعبئة', '53000');
    await photo('سند التحميل');
    await photo('العداد قبل التعبئة');
    await photo('العداد بعد التعبئة');
    await tapText('تأكيد التحميل');
    await settle(find.textContaining('«50,000»'));
    expect(find.textContaining('لا يمكن اعتماد القراءة'), findsOneWidget);

    // Correct readings → accepted
    await type('قراءة العداد قبل التعبئة', '50000');
    await type('قراءة العداد بعد التعبئة', '54000');
    await tapText('تأكيد التحميل');
    await settle(find.text('بدء الرحلة'));

    await tapText('بدء الرحلة');
    await settle(find.text('تأكيد الوصول'));
    await tapText('تأكيد الوصول');
    await settle(find.text('بدء التفريغ'));

    await photo('الأختام');
    await tapText('بدء التفريغ');
    await settle(find.text('إرسال التسليم'));

    await type('عداد التفريغ قبل', '54000');
    await type('عداد التفريغ بعد', '58000');
    await type('الكمية حسب سند الاستلام (لتر)', '4000');
    await type('رقم سند الاستلام', 'APP-RCPT-1');
    await type('المتبقي في الخزان (لتر)', '0');
    await type('المُعاد (لتر)', '0');
    await photo('عداد التفريغ قبل');
    await photo('عداد التفريغ بعد');
    await photo('سند الاستلام الموقّع');
    await tapText('إرسال التسليم');
    await settle(find.text('تم إرسال الطلب، وهو بانتظار اعتماد المشرف.'));

    expect(camera.taken, 7);
  }, timeout: const Timeout(Duration(minutes: 5)), skip: user.isEmpty);   // runs only with a live server (see tests/app-flow.mjs)
}
