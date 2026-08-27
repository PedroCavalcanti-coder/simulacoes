using UnityEngine;

namespace LiquidFX
{
    /// <summary>
    /// Fixed size ring buffer holding liquid that has left the source but has not landed yet.
    /// This is what makes the destination fill with the real delay of the fall instead of
    /// filling instantly (or, worse, filling from particle collision callbacks).
    /// No allocation after construction.
    ///
    /// Each packet carries which <see cref="LiquidDefinition"/> it is (null for a source that has
    /// no asset, e.g. a plain valve) so a layered flask that changes which liquid it is pouring
    /// mid-stream does not lose that identity while the liquid is still in the air.
    /// </summary>
    public sealed class LiquidFlightQueue
    {
        struct Packet
        {
            public float Millilitres;
            public float ArrivalTime;
            public LiquidDefinition Liquid;
        }

        readonly Packet[] packets;
        int head;
        int count;
        float inFlightML;

        public LiquidFlightQueue(int capacity = 48)
        {
            packets = new Packet[Mathf.Max(4, capacity)];
        }

        /// <summary>Liquid currently between the lip and the target. Needed to audit conservation.</summary>
        public float InFlightML => inFlightML;

        public int Count => count;

        public void Clear()
        {
            head = 0;
            count = 0;
            inFlightML = 0f;
        }

        /// <summary>
        /// Queues liquid that will land at <paramref name="arrivalTime"/>. When the buffer is full
        /// the oldest packet is merged into the newest one so volume is never lost; if the two
        /// packets are different liquids the newest one's identity wins (conservation of volume
        /// matters more than fidelity of identity here, same as the original single-liquid design).
        /// </summary>
        public void Enqueue(float millilitres, float arrivalTime, LiquidDefinition liquid = null)
        {
            if (millilitres <= 0f)
                return;

            if (count == packets.Length)
            {
                int tail = (head + count - 1) % packets.Length;
                packets[tail].Millilitres += millilitres;
                packets[tail].Liquid = liquid;
                inFlightML += millilitres;
                return;
            }

            int index = (head + count) % packets.Length;
            packets[index].Millilitres = millilitres;
            packets[index].ArrivalTime = arrivalTime;
            packets[index].Liquid = liquid;
            count++;
            inFlightML += millilitres;
        }

        /// <summary>
        /// Pops one packet that has landed by <paramref name="now"/>, if any. Call in a loop
        /// (<c>while (TryDequeueArrived(...))</c>) to drain everything that has arrived - a single
        /// frame can land more than one packet, potentially of different liquids.
        /// </summary>
        public bool TryDequeueArrived(float now, out float millilitres, out LiquidDefinition liquid)
        {
            if (count == 0 || packets[head].ArrivalTime > now)
            {
                millilitres = 0f;
                liquid = null;
                return false;
            }

            millilitres = packets[head].Millilitres;
            liquid = packets[head].Liquid;
            head = (head + 1) % packets.Length;
            count--;
            inFlightML = Mathf.Max(0f, inFlightML - millilitres);
            return true;
        }

        /// <summary>Pops everything regardless of arrival time. Used when a stream is cut short.</summary>
        public float DrainAll()
        {
            float remaining = inFlightML;
            Clear();
            return remaining;
        }
    }
}
