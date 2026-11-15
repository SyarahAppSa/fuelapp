import 'package:flutter/material.dart';

import '../api.dart';
import 'home.dart';

class LoginScreen extends StatefulWidget {
  const LoginScreen({super.key});
  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends State<LoginScreen> {
  final _user = TextEditingController();
  final _pass = TextEditingController();
  final _code = TextEditingController();
  final _server = TextEditingController(text: serverAddress);
  MfaChallenge? _challenge;
  String? _error;
  bool _busy = false;

  Future<void> _submit() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      if (allowServerChange && _challenge == null) {
        final err = await setServerAddress(_server.text);
        if (err != null) throw ApiException(400, err);
      }
      if (_challenge == null) {
        final c = await Api.instance.login(_user.text.trim(), _pass.text);
        if (c != null) {
          setState(() => _challenge = c);
          return;
        }
      } else {
        await Api.instance.verifyMfa(_challenge!, _code.text.trim());
      }
      _pass.clear();
      if (!mounted) return;
      Navigator.of(context).pushReplacement(MaterialPageRoute(builder: (_) => const HomeScreen()));
    } catch (e) {
      setState(() => _error = e.toString());
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final t = Theme.of(context);
    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 420),
              child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                Image.asset('assets/logo_full.png', height: 72),
                const SizedBox(height: 24),
                Text('مرحبًا بك مجددًا', style: t.textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w800), textAlign: TextAlign.center),
                const SizedBox(height: 6),
                Text('تطبيق السائق — منظومة فيول آب', style: t.textTheme.bodyMedium, textAlign: TextAlign.center),
                const SizedBox(height: 28),
                if (_challenge == null) ...[
                  if (allowServerChange) ...[
                    TextField(controller: _server, decoration: const InputDecoration(labelText: 'عنوان الخادم (نسخة الاختبار)'), keyboardType: TextInputType.url, textDirection: TextDirection.ltr),
                    const SizedBox(height: 12),
                  ],
                  TextField(controller: _user, decoration: const InputDecoration(labelText: 'اسم المستخدم'), autofillHints: const [AutofillHints.username], maxLength: 64),
                  const SizedBox(height: 12),
                  TextField(controller: _pass, decoration: const InputDecoration(labelText: 'كلمة المرور'), obscureText: true, enableSuggestions: false, autocorrect: false, maxLength: 128, onSubmitted: (_) => _submit()),
                ] else ...[
                  const Text('أدخل الرمز المكوّن من 6 أرقام من تطبيق المصادقة، أو أحد رموز الاسترداد.'),
                  const SizedBox(height: 12),
                  TextField(controller: _code, decoration: const InputDecoration(labelText: 'رمز التحقق'), keyboardType: TextInputType.number, autofillHints: const [AutofillHints.oneTimeCode], maxLength: 20, onSubmitted: (_) => _submit()),
                ],
                if (_error != null) Padding(padding: const EdgeInsets.only(top: 12), child: Text(_error!, style: TextStyle(color: t.colorScheme.error))),
                const SizedBox(height: 20),
                FilledButton(onPressed: _busy ? null : _submit, child: Text(_busy ? 'جارٍ الدخول…' : _challenge == null ? 'تسجيل الدخول' : 'تحقق')),
              ]),
            ),
          ),
        ),
      ),
    );
  }
}
