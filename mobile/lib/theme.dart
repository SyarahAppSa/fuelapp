import 'package:flutter/material.dart';

/// FuelUp identity — same tokens as the web dashboard (index.css).
class Brand {
  static const brand = Color(0xFF1B1FA8);
  static const deep = Color(0xFF00009A);
  static const red = Color(0xFFB50D12);
  static const cyan = Color(0xFF0AA5B8);
  static const ok = Color(0xFF1D7A46);
  static const warn = Color(0xFFA45F00);
  static const crit = Color(0xFFC62828);
  static const heroGradient = LinearGradient(colors: [Color(0xFF00009A), Color(0xFF2430C9), Color(0xFF0AA5B8)], begin: Alignment.topRight, end: Alignment.bottomLeft);
}

ThemeData buildTheme(Brightness b) {
  final dark = b == Brightness.dark;
  final scheme = ColorScheme.fromSeed(
    seedColor: Brand.brand,
    brightness: b,
    primary: dark ? const Color(0xFF7D86FF) : Brand.brand,
    error: dark ? const Color(0xFFFF7A70) : Brand.crit,
    surface: dark ? const Color(0xFF141A3D) : Colors.white,
  );
  return ThemeData(
    useMaterial3: true,
    fontFamily: 'Tajawal',
    colorScheme: scheme,
    scaffoldBackgroundColor: dark ? const Color(0xFF0B0F2A) : const Color(0xFFF3F4FA),
    cardTheme: CardThemeData(elevation: 0, shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14), side: BorderSide(color: dark ? const Color(0xFF283063) : const Color(0xFFE3E6F3)))),
    inputDecorationTheme: InputDecorationTheme(border: OutlineInputBorder(borderRadius: BorderRadius.circular(10)), filled: true, fillColor: dark ? const Color(0xFF1A2150) : const Color(0xFFF5F6FC)),
    filledButtonTheme: FilledButtonThemeData(style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(48), shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)))),
  );
}

Color statusColor(String s) => switch (s) {
      'Approved' => Brand.ok,
      'UnderReview' || 'Cancelled' => Brand.crit,
      'AwaitingApproval' => Brand.warn,
      _ => Brand.brand,
    };
