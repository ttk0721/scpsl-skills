// Fixture for SL018 / SL019: per-raise work inside handlers of hot, conditionally hot and burst events.
// The subscription to a hot event is only SL001 (warn); the work inside is what the rules report.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.IO;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.CustomHandlers;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using MEC;
using UnityEngine;

namespace Fixture
{
    public class HotWork : CustomEventsHandler
    {
        private static readonly HashSet<int> Watched = new HashSet<int>();
        private static readonly object Gate = new object();
        private static readonly HttpClient Client = new HttpClient();
        private static int counter;
        private static bool enabled;

        // hot (per-frame-per-pair): every line below is one SL018, labelled with what was found
        public override void OnPlayerValidatedVisibility(PlayerValidatedVisibilityEventArgs ev) // expect: SL001
        {
            Logger.Debug("pair"); // expect: SL018
            string key = ev.Player.Nickname + ":" + ev.Target.Nickname; // expect: SL018
            string text = $"{ev.Player.Nickname}"; // expect: SL018
            var list = new List<Player>(); // expect: SL018
            var anon = new { Id = 1 }; // expect: SL018
            var buffer = new byte[16]; // expect: SL018
            var names = Player.ReadyList.Select(p => p.Nickname); // expect: SL018
            foreach (Player other in Player.List) // expect: SL018
            {
                counter++;
            }
            int n = Player.Count; // expect: SL018
            lock (Gate) { counter++; } // expect: SL018
            if (!File.Exists("flag.txt")) // expect: SL018
                return;
            var pending = Client.GetStringAsync("http://example.invalid/"); // expect: SL018
            File.WriteAllText("pair.txt", "x"); // expect: SL007
            Timing.CallDelayed(1f, () => { }); // expect: SL018

            // Cheap shapes that must stay quiet: a flag test, a set lookup, value types, math, O(1) counts.
            if (!enabled)
                return;
            counter++;
            bool known = Watched.Contains(ev.Player.PlayerId);
            Vector3 up = new Vector3(0f, 1f, 0f);
            Quaternion rot = new Quaternion(0f, 0f, 0f, 1f);
            int most = Math.Max(1, 2);
            float least = Mathf.Min(1f, 2f);
            int cached = Player.List.Count;
            int total = counter + 1;
        }

        // conditionally hot (Hurting is per-frame for damage-over-time effects): same rule, same severity
        public override void OnPlayerHurting(PlayerHurtingEventArgs ev) // expect: SL001
        {
            Logger.Warn("hurt"); // expect: SL018
            if (ev.Attacker == null)
                ev.IsAllowed = false;
        }

        // burst (per shot / per hit): the same work is a warning, SL019
        public override void OnPlayerShootingWeapon(PlayerShootingWeaponEventArgs ev)
        {
            Logger.Info($"{ev.Player.Nickname} shot"); // expect: SL019
            Timing.RunCoroutine(Later(ev)); // expect: SL019
            foreach (Player p in Player.ReadyList) // expect: SL019
            {
                counter++;
            }
            if (ev.Player == null)
                return;
            ev.IsAllowed = enabled;
        }

        public override void OnPlayerPlacingBulletHole(PlayerPlacingBulletHoleEventArgs ev)
        {
            var copy = Watched.ToList(); // expect: SL019
        }

        // not a hot, conditionally hot or burst event: the rule stays out of it
        public override void OnPlayerJoined(PlayerJoinedEventArgs ev)
        {
            Logger.Info("joined " + ev.Player.Nickname);
            var all = Player.ReadyList.ToList();
            lock (Gate) { counter++; }
        }

        private IEnumerator<float> Later(PlayerShootingWeaponEventArgs ev) { yield break; }
    }

    public class StaticSubscriptions
    {
        public void Enable()
        {
            PlayerEvents.TriggeringTesla += OnTesla; // expect: SL001
            PlayerEvents.ShotWeapon += ev => { Logger.Info("shot"); }; // expect: SL019
            PlayerEvents.SendingHitmarker += OnHitmarker;
        }

        private void OnTesla(PlayerTriggeringTeslaEventArgs ev)
        {
            throw new InvalidOperationException("no"); // expect: SL018
        }

        private void OnHitmarker(PlayerSendingHitmarkerEventArgs ev)
        {
            string s = string.Format("{0}", ev.Player); // expect: SL019
        }

        // The same constructs outside any handler are not this rule's business.
        public void Report(Player p)
        {
            Logger.Debug("report " + p.Nickname);
            var list = new List<Player>();
            lock (this) { }
        }
    }
}
