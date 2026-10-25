using System;
using System.Collections.Generic;

namespace SC6000DualMonitor
{
    internal sealed class NgCaptureEventArgs : EventArgs
    {
        public uint Frame;
        public DateTime TimeUtc;
        public string ProcedureGuid;
    }
    internal sealed class NgFrameMatcher
    {
        private sealed class State { public bool? Ng, Normal; public bool Delivered; }
        private readonly Dictionary<uint, State> _states = new Dictionary<uint, State>();
        private readonly Queue<uint> _order = new Queue<uint>();
        // Called under the session callback lock. Accept either callback arrival order.
        public bool Record(uint frame, bool? ng, bool? normal)
        {
            State state;
            if (!_states.TryGetValue(frame, out state))
            {
                state = new State(); _states.Add(frame, state); _order.Enqueue(frame);
                while (_order.Count > 128) _states.Remove(_order.Dequeue());
            }
            if (ng.HasValue) state.Ng = ng;
            if (normal.HasValue) state.Normal = normal;
            if (state.Delivered || state.Ng != true || state.Normal != true) return false;
            state.Delivered = true; return true;
        }
        public void Clear() { _states.Clear(); _order.Clear(); }
    }
}
