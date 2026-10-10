using System;
using System.Linq;

namespace Drawmaha
{
    public enum EntryMode { Ready, WaitForBigBlind, PostBigBlind }
    // Dead-button cash-game policy. Seat admission never recomputes the old blinds.
    public sealed class BlindOrbit
    {
        public int LastSmallBlind = -1, LastBigBlind = -1;
        public bool Started;
        static int Next(int from, bool[] available)
        {
            for (int n = 1; n <= available.Length; n++) { int i = (from + n + available.Length) % available.Length; if (available[i]) return i; }
            return -1;
        }
        static bool Protected(int seat, int button, int big, int count)
        {
            // Entry prohibited on the button or in the clockwise button-to-BB interval.
            int distance = (seat - button + count) % count;
            int bbDistance = (big - button + count) % count;
            return distance < bbDistance;
        }
        public HandSetup Plan(bool[] available, EntryMode[] entry)
        {
            if (available.Length != entry.Length || available.Count(a => a) < 2) return null;
            int count = available.Length;
            var plan = new HandSetup { Active = new bool[count] };
            if (!Started)
            {
                Array.Copy(available, plan.Active, count); plan.Dealer = Next(-1, available);
                plan.SmallBlind = available.Count(a => a) == 2 ? plan.Dealer : Next(plan.Dealer, available);
                plan.BigBlind = Next(plan.SmallBlind, available); return plan;
            }
            plan.BigBlind = Next(LastBigBlind, available);
            plan.SmallBlind = LastBigBlind;
            plan.Dealer = LastSmallBlind >= 0 ? LastSmallBlind : (LastBigBlind - 1 + count) % count;
            for (int i = 0; i < count; i++)
                plan.Active[i] = available[i] && (entry[i] == EntryMode.Ready || i == plan.BigBlind || (entry[i] == EntryMode.PostBigBlind && !Protected(i, plan.Dealer, plan.BigBlind, count)));
            // Restarting a two-player table: the newcomer is the BB, the incumbent the button.
            if (plan.Active.Count(a => a) < 2 && available.Count(a => a) == 2)
            {
                int newcomer = Enumerable.Range(0, count).FirstOrDefault(i => available[i] && entry[i] != EntryMode.Ready);
                plan.BigBlind = newcomer; Array.Copy(available, plan.Active, count);
            }
            if (plan.Active.Count(a => a) < 2) return null;
            if (plan.Active.Count(a => a) == 2)
            {
                plan.Dealer = Next(plan.BigBlind, plan.Active); plan.SmallBlind = plan.Dealer;
            }
            else if (!plan.Active[plan.SmallBlind]) plan.SmallBlind = -1; // dead small blind
            for (int i = 0; i < count; i++)
                if (plan.Active[i] && entry[i] != EntryMode.Ready && i != plan.BigBlind && i != plan.SmallBlind) plan.EntryPosts[i] = 1; // caller supplies BB amount
            return plan;
        }
        public void Commit(HandSetup plan)
        {
            int previousBigBlind = LastBigBlind;
            Started = true; LastBigBlind = plan.BigBlind;
            // Keep the physical previous BB seat even when its small blind is dead.
            LastSmallBlind = plan.SmallBlind >= 0 ? plan.SmallBlind : previousBigBlind;
        }
    }
}
