import 'package:flutter_test/flutter_test.dart';
import 'package:fuelup_driver/api.dart';
import 'package:fuelup_driver/main.dart';

void main() {
  test('parses numbers typed with thousands separators', () {
    expect(parseNum('125,000'), 125000);
    expect(fmt(3000), '3,000');
  });

  testWidgets('login screen renders in Arabic', (tester) async {
    await tester.pumpWidget(const FuelUpApp(signedIn: false));
    await tester.pumpAndSettle();
    expect(find.text('تسجيل الدخول'), findsOneWidget);
    expect(find.text('اسم المستخدم'), findsOneWidget);
  });
}
