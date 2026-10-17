using System;

namespace Drawmaha
{
    public interface IHandAudit { void Record(HandAuditEvent entry); }

    // Server-only data: never include these objects in a client envelope.
    public sealed class HandAuditEvent
    {
        public string Kind, Stage, Detail;
        public int Hand, Seat = -1, Amount, Position = -1, Dealer, SmallBlindSeat, BigBlindSeat;
        public int SmallBlind, BigBlind, Actor, CurrentBet, LastRaise;
        public int[] Indices;
        public Card[] Cards, Board;
        public AuditPlayer[] Players;
        public HandSetup Setup;
    }
    public sealed class AuditPlayer
    {
        public string Name;
        public int Seat, Stack, Bet, Contributed, Payout;
        public bool InHand, Folded;
        public Card[] Cards;
    }
    public sealed class AuditFailureException : Exception
    {
        public AuditFailureException(Exception inner) : base("Table paused: secure hand recording failed.", inner) { }
    }
}
