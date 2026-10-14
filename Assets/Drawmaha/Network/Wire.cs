using System;

namespace Drawmaha.Network
{
    public static class WireProtocol
    {
        public const int Current = 7;
        public const int WaitingPreferences = 5;
        public const int MainBank = 7;
    }

    [Serializable] public class Command
    {
        public string type, username, token, walletId, accessKey, tableId, entryMode, id;
        public int avatar, raiseTo, hand;
        public long revision;
        public bool enabled;
        public int callAmount;
        public int[] discards;
    }
    [Serializable] public class Envelope
    {
        public string type, message, token, epoch, id;
        public int protocol, walletBalance;
        public LobbyRow[] tables;
        public TableState table;
    }
    [Serializable] public class LobbyRow
    {
        public string id, name;
        public int smallBlind, bigBlind, buyIn, occupied, available, maxSeats;
    }
    [Serializable] public class CardView { public int rank, suit; }
    [Serializable] public class HandHistoryPlayerView
    {
        public string username;
        public int avatar;
        public bool folded, revealed;
        public CardView[] cards;
    }
    [Serializable] public class HandHistoryView
    {
        public int hand;
        public bool showdown;
        public CardView[] board;
        public HandHistoryPlayerView[] players;
    }
    [Serializable] public class SeatView
    {
        public int seat, avatar, stack, bet, count, drawCount;
        public string username, status, entryMode;
        public bool occupied, connected, inHand, folded, leaving, winner, scoop, needsRebuy, drawLocked, checkFold, callQueued, waitingToAct;
        public int callAmount, queuedDrawCount;
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
        public HandHistoryView[] handHistory;
        public string[] results, history;
    }
}
