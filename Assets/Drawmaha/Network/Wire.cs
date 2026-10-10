using System;

namespace Drawmaha.Network
{
    [Serializable] public class Command
    {
        public string type, username, token, accessKey, tableId, entryMode, id;
        public int avatar, raiseTo, hand;
        public long revision;
        public int[] discards;
    }
    [Serializable] public class Envelope
    {
        public string type, message, token, epoch, id;
        public LobbyRow[] tables;
        public TableState table;
    }
    [Serializable] public class LobbyRow
    {
        public string id, name;
        public int smallBlind, bigBlind, buyIn, occupied, available, maxSeats;
    }
    [Serializable] public class CardView { public int rank, suit; }
    [Serializable] public class SeatView
    {
        public int seat, avatar, stack, bet, count, drawCount;
        public string username, status, entryMode;
        public bool occupied, connected, inHand, folded, leaving, winner, scoop;
        public CardView[] cards;
    }
    [Serializable] public class TableState
    {
        public string id, name, stage;
        public int yourSeat, hand, dealer, smallBlindSeat, bigBlindSeat, smallBlind, bigBlind, pot, actor, call, minRaise, maxRaise;
        public long revision, deadlineUnixMs, serverUnixMs;
        public bool canRaise, showdown;
        public SeatView[] players;
        public CardView[] board;
        public string[] results, history;
    }
}
