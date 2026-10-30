using System;
using System.Collections.Generic;

namespace Fomoxa.Rpc
{
    public sealed record Outgoing(uint MessageId, byte[] Payload);

    public enum Delivery
    {
        Accepted,
        Rejected,
    }

    public sealed class Outbox
    {
        private readonly Queue<(uint Call, Outgoing Frame)> frames = new Queue<(uint, Outgoing)>();
        private readonly Dictionary<uint, int> perCall = new Dictionary<uint, int>();
        private readonly int maxPerCall;
        private readonly int maxTotal;

        public Outbox(int maxPerCall, int maxTotal)
        {
            this.maxPerCall = maxPerCall;
            this.maxTotal = maxTotal;
        }

        public int Count => frames.Count;

        public bool IsEmpty => frames.Count == 0;

        public int QueuedFor(CallId call) => perCall.TryGetValue(call.Value, out int count) ? count : 0;

        public bool IsFullFor(CallId call) => QueuedFor(call) >= maxPerCall;

        public bool HasRoom(CallId call) => frames.Count < maxTotal && !IsFullFor(call);

        public bool RequestStillQueued(CallId call)
        {
            foreach (var (queued, frame) in frames)
            {
                if (queued == call.Value)
                {
                    return frame.Payload.Length > 0 && Wire.KindBits(frame.Payload[0]) == (byte)Kind.Request;
                }
            }
            return false;
        }

        public void PushTerminal(CallId call, Outgoing frame) => Append(call.Value, frame);

        public bool Push(CallId call, Outgoing frame)
        {
            if (frames.Count >= maxTotal || IsFullFor(call))
            {
                return false;
            }
            Append(call.Value, frame);
            return true;
        }

        public void Discard(CallId call)
        {
            if (!perCall.Remove(call.Value))
            {
                return;
            }
            int before = frames.Count;
            for (int index = 0; index < before; index++)
            {
                var entry = frames.Dequeue();
                if (entry.Call != call.Value)
                {
                    frames.Enqueue(entry);
                }
            }
        }

        public void Clear()
        {
            frames.Clear();
            perCall.Clear();
        }

        public void Drain(Func<Outgoing, Delivery> sink)
        {
            while (frames.Count > 0)
            {
                var (call, frame) = frames.Peek();
                if (sink(frame) == Delivery.Rejected)
                {
                    return;
                }
                frames.Dequeue();
                Release(call);
            }
        }

        private void Append(uint call, Outgoing frame)
        {
            perCall[call] = QueuedFor(new CallId(call)) + 1;
            frames.Enqueue((call, frame));
        }

        private void Release(uint call)
        {
            if (perCall.TryGetValue(call, out int count) && count > 1)
            {
                perCall[call] = count - 1;
            }
            else
            {
                perCall.Remove(call);
            }
        }
    }
}
