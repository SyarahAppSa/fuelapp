import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';

import 'api.dart';
import 'screens/home.dart';
import 'screens/login.dart';
import 'theme.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final signedIn = await Api.instance.restore();
  runApp(FuelUpApp(signedIn: signedIn));
}

class FuelUpApp extends StatelessWidget {
  const FuelUpApp({super.key, required this.signedIn});
  final bool signedIn;

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'فيول آب — السائق',
      debugShowCheckedModeBanner: false,
      theme: buildTheme(Brightness.light),
      darkTheme: buildTheme(Brightness.dark),
      locale: const Locale('ar'),
      supportedLocales: const [Locale('ar')],
      localizationsDelegates: const [GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
      home: signedIn ? const HomeScreen() : const LoginScreen(),
    );
  }
}
