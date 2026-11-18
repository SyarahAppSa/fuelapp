import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter/painting.dart';
import 'package:image/image.dart' as img;

/// Burns the capture date, time and place into the photo itself (bottom band), so the evidence travels with the picture
/// even when it is downloaded or forwarded. Arabic text is drawn with the app's font; the result is a JPEG.
class PhotoStamp {
  /// Replaceable in tests.
  static Future<Uint8List> Function(Uint8List photo, List<String> lines) apply = _apply;

  static String two(int v) => v.toString().padLeft(2, '0');
  /// Left-to-right isolate: keeps numbers in their order inside the Arabic line.
  static String ltr(String s) => '\u2066$s\u2069';
  static String when(DateTime t) => ltr('${two(t.day)}/${two(t.month)}/${t.year}  ${two(t.hour)}:${two(t.minute)}:${two(t.second)}');

  static Future<Uint8List> _apply(Uint8List photo, List<String> lines) async {
    final codec = await ui.instantiateImageCodec(photo, targetWidth: null);
    final src = (await codec.getNextFrame()).image;
    final w = src.width.toDouble(), h = src.height.toDouble();
    final fontSize = (w / 32).clamp(14.0, 64.0);
    final pad = fontSize * .6;

    final painters = [
      for (final (i, l) in lines.indexed)
        TextPainter(
          text: TextSpan(text: l, style: TextStyle(color: const Color(0xFFFFFFFF), fontFamily: 'Tajawal', fontSize: i == 0 ? fontSize * 1.1 : fontSize, fontWeight: i == 0 ? FontWeight.w800 : FontWeight.w700)),
          textDirection: TextDirection.rtl,
          textAlign: TextAlign.right,
        )..layout(maxWidth: w - pad * 2),
    ];
    final band = painters.fold<double>(0, (s, p) => s + p.height + fontSize * .15) + pad * 2;

    final rec = ui.PictureRecorder();
    final c = ui.Canvas(rec);
    c.drawImage(src, ui.Offset.zero, ui.Paint());
    c.drawRect(ui.Rect.fromLTWH(0, h - band, w, band), ui.Paint()..color = const Color(0xB3000000));
    var y = h - band + pad;
    for (final p in painters) {
      p.paint(c, ui.Offset(w - pad - p.width, y));
      y += p.height + fontSize * .15;
    }
    final out = await rec.endRecording().toImage(src.width, src.height);
    final rgba = (await out.toByteData(format: ui.ImageByteFormat.rawRgba))!;
    final jpg = img.encodeJpg(img.Image.fromBytes(width: src.width, height: src.height, bytes: rgba.buffer, numChannels: 4), quality: 82);
    src.dispose();
    out.dispose();
    return Uint8List.fromList(jpg);
  }
}
