import 'package:flutter/material.dart';

import '../api.dart';
import '../theme.dart';
import '../fill/fill_screen.dart';
import '../fill/outbox.dart';
import 'login.dart';
import 'trip.dart';

/// Driver home: الطلب الجاري / طلباتي / الطلبات السابقة / مركباتي + notifications.
class HomeScreen extends StatefulWidget {
  const HomeScreen({super.key});
  @override
  State<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends State<HomeScreen> {
  int _tab = 0;
  Me? _me;
  List<TripView> _trips = [];
  List<Map<String, dynamic>> _notes = [];
  String? _error;
  bool _loading = true;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() => _loading = true);
    try {
      final api = Api.instance;
      final res = await Future.wait([api.get('/api/me'), api.get('/api/trips?size=100'), api.get('/api/notifications')]);
      setState(() {
        _me = Me(res[0] as Map<String, dynamic>);
        _trips = ((res[1] as Map)['items'] as List).map((x) => TripView(x as Map<String, dynamic>)).toList();
        _notes = (res[2] as List).cast<Map<String, dynamic>>();
        _error = null;
      });
    } on ApiException catch (e) {
      if (e.status == 401) return _signOut();
      setState(() => _error = e.message);
    } catch (e) {
      setState(() => _error = 'تعذر الاتصال بالخادم. تحقق من الشبكة.');
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _signOut() async {
    await FillOutbox.instance.sync();
    if (FillOutbox.instance.pending > 0 && mounted) {
      final go = await showDialog<bool>(context: context, builder: (_) => AlertDialog(title: const Text('تقارير لم تُرسل بعد'), content: Text('على الجهاز ${FillOutbox.instance.pending} عنصر بانتظار المزامنة. الخروج يحذفها.'), actions: [TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('بقاء')), TextButton(onPressed: () => Navigator.pop(context, true), child: const Text('خروج وحذف'))]));
      if (go != true) return;
    }
    await FillOutbox.instance.clear();
    await Api.instance.logout();
    if (!mounted) return;
    Navigator.of(context).pushAndRemoveUntil(MaterialPageRoute(builder: (_) => const LoginScreen()), (_) => false);
  }

  Future<void> _open(TripView t) async {
    await Navigator.of(context).push(MaterialPageRoute(builder: (_) => TripScreen(tripId: t.id, me: _me!)));
    _load();
  }

  int get _unread => _notes.where((n) => n['readAt'] == null).length;

  Future<void> _showNotes() async {
    await showModalBottomSheet<void>(
      context: context,
      showDragHandle: true,
      builder: (_) => ListView(
        padding: const EdgeInsets.all(16),
        children: [
          const Text('الإشعارات', style: TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
          if (_notes.isEmpty) const Padding(padding: EdgeInsets.all(24), child: Text('لا توجد إشعارات.')),
          for (final n in _notes)
            ListTile(
              leading: Icon(n['readAt'] == null ? Icons.circle : Icons.circle_outlined, size: 10, color: Brand.brand),
              title: Text(n['text'] as String? ?? ''),
              subtitle: Text(DateTime.tryParse(n['at'] as String? ?? '')?.toLocal().toString().substring(0, 16) ?? ''),
            ),
        ],
      ),
    );
    await Api.instance.post('/api/notifications/read-all');
    _load();
  }

  @override
  Widget build(BuildContext context) {
    final current = _trips.where((t) => t.isOpen && t.status != 'WaitingLoad').toList();
    final mine = _trips.where((t) => t.status == 'WaitingLoad').toList();
    final past = _trips.where((t) => !t.isOpen).toList();
    final pages = [
      const FillReportsTab(),
      _TripList(trips: current, empty: 'لا يوجد طلب جارٍ الآن.', onTap: _open),
      _TripList(trips: mine, empty: 'لا توجد طلبات جديدة مسندة إليك.', onTap: _open),
      _TripList(trips: past, empty: 'لا توجد طلبات سابقة.', onTap: _open),
      _Vehicles(vehicles: _me?.vehicles ?? []),
    ];
    return Scaffold(
      appBar: AppBar(
        title: Row(children: [Image.asset('assets/logo_mark.png', height: 28), const SizedBox(width: 8), Text(_me?.name ?? 'فيول آب')]),
        actions: [
          IconButton(onPressed: _showNotes, tooltip: 'الإشعارات', icon: Badge(isLabelVisible: _unread > 0, label: Text('$_unread'), child: const Icon(Icons.notifications_outlined))),
          IconButton(onPressed: _signOut, tooltip: 'خروج', icon: const Icon(Icons.logout)),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: _load,
        child: _tab == 0
            ? pages[0]
            : _loading && _me == null
            ? const Center(child: CircularProgressIndicator())
            : _error != null
                ? ListView(children: [Padding(padding: const EdgeInsets.all(24), child: Text(_error!, style: TextStyle(color: Theme.of(context).colorScheme.error)))])
                : pages[_tab],
      ),
      bottomNavigationBar: NavigationBar(
        selectedIndex: _tab,
        onDestinationSelected: (i) => setState(() => _tab = i),
        destinations: [
          const NavigationDestination(icon: Icon(Icons.fact_check_outlined), label: 'تقرير التعبئة'),
          NavigationDestination(icon: Badge(isLabelVisible: current.isNotEmpty, child: const Icon(Icons.local_shipping_outlined)), label: 'الطلب الجاري'),
          NavigationDestination(icon: Badge(isLabelVisible: mine.isNotEmpty, label: Text('${mine.length}'), child: const Icon(Icons.assignment_outlined)), label: 'طلباتي'),
          const NavigationDestination(icon: Icon(Icons.history), label: 'السابقة'),
          const NavigationDestination(icon: Icon(Icons.directions_car_outlined), label: 'مركباتي'),
        ],
      ),
    );
  }
}

class _TripList extends StatelessWidget {
  const _TripList({required this.trips, required this.empty, required this.onTap});
  final List<TripView> trips;
  final String empty;
  final void Function(TripView) onTap;

  @override
  Widget build(BuildContext context) {
    if (trips.isEmpty) return ListView(children: [Padding(padding: const EdgeInsets.all(32), child: Text(empty, textAlign: TextAlign.center))]);
    return ListView.separated(
      padding: const EdgeInsets.all(16),
      itemCount: trips.length,
      separatorBuilder: (_, __) => const SizedBox(height: 10),
      itemBuilder: (_, i) {
        final t = trips[i];
        return Card(
          child: ListTile(
            onTap: () => onTap(t),
            title: Text('${t.no} · ${t.customer}', style: const TextStyle(fontWeight: FontWeight.w700)),
            subtitle: Text('${t.orderNo != null ? '${t.orderLine}\n' : ''}${t.station} ← ${t.site}\nالكمية المطلوبة ${fmt(t.qtyRequested)} لتر'),
            isThreeLine: true,
            trailing: Chip(label: Text(t.label, style: const TextStyle(color: Colors.white, fontSize: 12)), backgroundColor: statusColor(t.status), side: BorderSide.none),
          ),
        );
      },
    );
  }
}

class _Vehicles extends StatelessWidget {
  const _Vehicles({required this.vehicles});
  final List<Vehicle> vehicles;

  @override
  Widget build(BuildContext context) {
    if (vehicles.isEmpty) return ListView(children: const [Padding(padding: EdgeInsets.all(32), child: Text('لا توجد مركبة مسندة إليك حاليًا. تواصل مع المشرف.', textAlign: TextAlign.center))]);
    return ListView(padding: const EdgeInsets.all(16), children: [
      for (final v in vehicles)
        Card(child: ListTile(leading: const Icon(Icons.local_shipping), title: Text(v.plate, style: const TextStyle(fontWeight: FontWeight.w800)), subtitle: Text('${v.model}\nالسعة ${fmt(v.capacity)} لتر'), isThreeLine: true)),
    ]);
  }
}
