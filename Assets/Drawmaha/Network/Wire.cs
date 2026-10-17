using System;

namespace Drawmaha.Network
{
    public static class WireProtocol
    {
        public const int Current = 11;
        public const int PrivateTables = 11;
        public const int ProfileUpdates = 9;
        public const int TableChat = 8;
        public const int WaitingPreferences = 5;
        public const int MainBank = 7;
    }

    [Serializable] public class Command
    {
        public string type, username, token, walletId, tableId, entryMode, id, message;
        public string tableName, inviteCode;
        public int avatar, hand, protocol;
        public float raiseTo, smallBlind, bigBlind, minBuyIn, maxBuyIn, buyInAmount;
        public long revision;
        public bool enabled, isPublic;
        public float callAmount;
        public int[] discards;
    }
    [Serializable] public class Envelope
    {
        public string type, message, token, epoch, id;
        public int protocol;
        public float walletBalance;
        public LobbyRow[] tables;
        public TableState table;
        public string tableId;
        public ChatMessageView chat;
        public ChatMessageView[] chatMessages;
        public string username;
        public int avatar;
    }
    public static class DisplayNameRules
    {
        public static string Validate(string value)
        {
            string name = (value ?? "").Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9][a-zA-Z0-9 _-]{1,19}$"))
                throw new ArgumentException("Use 2–20 letters, numbers, spaces, _ or -. Start with a letter or number.");
            return name;
        }
    }
    public static class ChatRules
    {
        public const int MaxCharacters = 200;
        public const int HistoryLimit = 30;
        public static int CharacterCount(string value)
        {
            int count = 0;
            for (int i = 0; i < (value ?? "").Length; i++, count++)
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
            return count;
        }
        public static string LimitInput(string value)
        {
            value = value ?? "";
            int i = 0, count = 0;
            for (; i < value.Length && count < MaxCharacters; i++, count++)
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
            if (i > 0 && char.IsHighSurrogate(value[i - 1])) i--;
            return value.Substring(0, i);
        }
        public static string Validate(string value)
        {
            if (value == null) throw new ArgumentException("Enter a chat message.");
            // Reject invalid Unicode and hidden line breaks rather than changing the message.
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsControl(c) || c == '\u2028' || c == '\u2029')
                    throw new ArgumentException("Chat messages must use a single line without control characters.");
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= value.Length || !char.IsLowSurrogate(value[++i])) throw new ArgumentException("Invalid chat text.");
                }
                else if (char.IsLowSurrogate(c)) throw new ArgumentException("Invalid chat text.");
            }
            if (CharacterCount(value) > MaxCharacters) throw new ArgumentException("Chat messages are limited to 200 characters.");
            value = value.Trim();
            if (value.Length == 0) throw new ArgumentException("Enter a chat message.");
            return value;
        }
    }
    [Serializable] public class ChatMessageView
    {
        public long sequence, sentUnixMs;
        public string username, message;
    }
    [Serializable] public class LobbyRow
    {
        public string id, name;
        public float smallBlind, bigBlind, buyIn, minBuyIn, maxBuyIn;
        public int occupied, available, maxSeats;
        public bool isPublic;
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
        public int seat, avatar, count, drawCount;
        public float stack, bet;
        public string username, status, entryMode;
        public bool occupied, connected, inHand, folded, leaving, winner, scoop, needsRebuy, drawLocked, checkFold, callQueued, waitingToAct;
        public float callAmount;
        public int queuedDrawCount;
        public CardView[] cards;
    }
    [Serializable] public class TableState
    {
        public string id, name, stage;
        public int yourSeat, hand, dealer, smallBlindSeat, bigBlindSeat, actor;
        public float smallBlind, bigBlind, minBuyIn, maxBuyIn, pot, call, minRaise, maxRaise;
        public long revision, deadlineUnixMs, serverUnixMs;
        public bool canRaise, showdown;
        public SeatView[] players;
        public CardView[] board;
        public HandHistoryView[] handHistory;
        public string[] results, history;
    }
}
