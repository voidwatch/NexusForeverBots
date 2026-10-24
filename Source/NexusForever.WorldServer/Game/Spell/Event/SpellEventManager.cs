using System.Collections.Generic;
using NexusForever.Shared;

namespace NexusForever.WorldServer.Game.Spell.Event
{
    public class SpellEventManager : IUpdate
    {
        public bool HasPendingEvent => events.Count != 0;

        /// <summary>Seconds until the next event fires, null without events (diagnostics).</summary>
        public double? NextDelay => events.Count == 0 ? null : System.Linq.Enumerable.Min(events, e => e.Delay);

        private readonly List<SpellEvent> events = new();

        public void Update(double lastTick)
        {
            foreach (SpellEvent spellEvent in events.ToArray())
            {
                spellEvent.Update(lastTick);
                if (spellEvent.Delay <= 0d)
                {
                    spellEvent.Callback.Invoke();
                    events.Remove(spellEvent);
                }
            }
        }

        public void EnqueueEvent(SpellEvent spellEvent)
        {
            events.Add(spellEvent);
        }

        public void CancelEvents()
        {
            events.Clear();
        }
    }
}
