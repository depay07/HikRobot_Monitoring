using System;
using System.Collections.Generic;

namespace SC6000DualMonitor
{
    internal sealed class NgCaptureEventArgs : EventArgs
    {
        public uint Frame;
        public DateTime TimeUtc;
        public string ProcedureGuid;
        public string Outcome = "NG", Detail;
    }
    internal sealed class NgFrameMatcher
    {
        private sealed class State { public bool? Ng, Normal; public bool Delivered, Error; }
        private readonly Dictionary<uint, State> _states = new Dictionary<uint, State>();
        private readonly Queue<uint> _order = new Queue<uint>();
        // Called under the session callback lock. Accept either callback arrival order.
        public bool Record(uint frame, bool? ng, bool? normal, bool conditionError = false)
        {
            State state;
            if (!_states.TryGetValue(frame, out state))
            {
                state = new State(); _states.Add(frame, state); _order.Enqueue(frame);
                while (_order.Count > 128) _states.Remove(_order.Dequeue());
            }
            if (ng.HasValue) state.Ng = ng;
            if (normal.HasValue) state.Normal = normal;
            state.Error |= conditionError;
            if (state.Delivered || !state.Normal.HasValue || (state.Normal.Value && state.Ng != true && !state.Error)) return false;
            state.Delivered = true; return true;
        }
        public string Outcome(uint frame) { State state; return _states.TryGetValue(frame, out state) && (state.Normal == false || state.Error) ? "ERROR" : "NG"; }
        public void Clear() { _states.Clear(); _order.Clear(); }
    }
}
