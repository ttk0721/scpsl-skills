using System.Collections.Generic;
using LabApi.Features.Wrappers;
using MEC;
using UnityEngine;

namespace Fixture
{
    public class PerFrame : MonoBehaviour
    {
        private void Update()
        {
            foreach (Player p in Player.List) // expect: SL011
            {
                p.SendHint("hp", 1f); // expect: SL012
            }
            int n = Player.List.Count; // a Count read is not an enumeration
        }

        private void LateUpdate()
        {
            var hubs = ReferenceHub.AllHubs; // expect: SL011
            Server.SendBroadcast("tick", 1); // expect: SL012
        }

        // a throttled Update is not per-frame work
        private void FixedUpdate()
        {
            if (Time.time < next)
                return;
            next = Time.time + 1f;
            foreach (Player p in Player.ReadyList) { p.SendHint("ok", 1f); }
        }

        private float next;

        private IEnumerator<float> EveryFrame()
        {
            while (true)
            {
                foreach (var p in Player.ReadyList) // expect: SL011
                {
                    p.ShowHint("x"); // expect: SL012
                }
                yield return Timing.WaitForOneFrame;
            }
        }

        private IEnumerator<float> OncePerSecond()
        {
            while (true)
            {
                foreach (var p in Player.ReadyList)
                {
                    p.SendHint("x", 1f);
                }
                yield return Timing.WaitForSeconds(1f);
            }
        }

        private IEnumerator<float> EveryFrameThrottled()
        {
            int frame = 0;
            while (true)
            {
                frame++;
                if (frame % 30 == 0)
                {
                    foreach (var p in Player.ReadyList) { p.SendHint("x", 1f); }
                }
                yield return Timing.WaitForOneFrame;
            }
        }

        // Update() with parameters is some other method, not the Unity message
        private void Update(Player target)
        {
            target.SendHint("fine", 1f);
            var all = Player.List;
        }

        public void NotPerFrame()
        {
            foreach (Player p in Player.List) { p.SendHint("once", 3f); }
        }
    }
}
