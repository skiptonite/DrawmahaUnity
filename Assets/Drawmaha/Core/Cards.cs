using System;
using System.Collections.Generic;
using System.Linq;

namespace Drawmaha
{
    public struct Card : IEquatable<Card>
    {
        public readonly int Rank; // 2..14 (ace)
        public readonly int Suit; // clubs, diamonds, hearts, spades
        public Card(int rank, int suit) { Rank = rank; Suit = suit; }
        public bool IsRed { get { return Suit == 1 || Suit == 2; } }
        public string RankText { get { return Rank <= 10 ? Rank.ToString() : "JQKA"[Rank - 11].ToString(); } }
        public string SuitText { get { return new[] { "♣", "♦", "♥", "♠" }[Suit]; } }
        public bool Equals(Card c) { return Rank == c.Rank && Suit == c.Suit; }
        public override bool Equals(object obj) { return obj is Card && Equals((Card)obj); }
        public override int GetHashCode() { return Suit * 13 + Rank - 2; }
        public override string ToString() { return RankText + SuitText; }
    }

    public sealed class Deck
    {
        readonly List<Card> cards = new List<Card>();
        int cursor;
        readonly Action<string, Card[], int> audit;
        public int Remaining { get { return cards.Count - cursor; } }
        public Deck(IRandomSource random, Action<string, Card[], int> audit = null)
        {
            if (random == null) throw new ArgumentNullException("random");
            this.audit = audit;
            for (int s = 0; s < 4; s++) for (int r = 2; r <= 14; r++) cards.Add(new Card(r, s));
            for (int i = cards.Count - 1; i > 0; i--)
            {
                int j = random.NextBelow(i + 1);
                if (j < 0 || j > i) throw new InvalidOperationException("Random source returned an out-of-range value.");
                Card c = cards[i]; cards[i] = cards[j]; cards[j] = c;
            }
            if (audit != null) audit("shuffle", cards.ToArray(), -1);
        }
        public void Burn() { TakeCard("burn"); }
        public Card Take() { return TakeCard("deal"); }
        Card TakeCard(string kind)
        {
            if (cursor >= cards.Count) throw new InvalidOperationException("Fixed deck exhausted. Cards must never be recycled.");
            Card result = cards[cursor];
            if (audit != null) audit(kind, new[] { result }, cursor);
            cursor++;
            return result;
        }
    }

    public struct HandValue : IComparable<HandValue>
    {
        public readonly int Score;
        public readonly int Category;
        public HandValue(int category, params int[] ranks)
        {
            Category = category;
            int value = category;
            for (int i = 0; i < 5; i++) value = value * 15 + (i < ranks.Length ? ranks[i] : 0);
            Score = value;
        }
        public string Name { get { return new[] { "High card", "One pair", "Two pair", "Three of a kind", "Straight", "Flush", "Full house", "Four of a kind", "Straight flush" }[Category]; } }
        public int CompareTo(HandValue other) { return Score.CompareTo(other.Score); }
        public override string ToString() { return Name; }
    }

    public static class HandEvaluator
    {
        public static HandValue Five(IList<Card> cards)
        {
            if (cards.Count != 5) throw new ArgumentException("Exactly five cards required.");
            int[] ranks = cards.Select(c => c.Rank).OrderByDescending(r => r).ToArray();
            var groups = ranks.GroupBy(r => r).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).ToArray();
            bool flush = cards.All(c => c.Suit == cards[0].Suit);
            int straight = 0;
            if (ranks.Distinct().Count() == 5)
            {
                if (ranks[0] - ranks[4] == 4) straight = ranks[0];
                else if (ranks.SequenceEqual(new[] { 14, 5, 4, 3, 2 })) straight = 5;
            }
            if (flush && straight > 0) return new HandValue(8, straight);
            if (groups[0].Count() == 4) return new HandValue(7, groups[0].Key, groups[1].Key);
            if (groups[0].Count() == 3 && groups[1].Count() == 2) return new HandValue(6, groups[0].Key, groups[1].Key);
            if (flush) return new HandValue(5, ranks);
            if (straight > 0) return new HandValue(4, straight);
            if (groups[0].Count() == 3) return new HandValue(3, groups.Select(g => g.Key).ToArray());
            if (groups[0].Count() == 2 && groups[1].Count() == 2) return new HandValue(2, groups[0].Key, groups[1].Key, groups[2].Key);
            if (groups[0].Count() == 2) return new HandValue(1, groups.Select(g => g.Key).ToArray());
            return new HandValue(0, ranks);
        }

        public static HandValue Omaha(IList<Card> hole, IList<Card> board)
        {
            if (hole.Count != 5 || board.Count < 3 || board.Count > 5) throw new ArgumentException("Omaha needs five hole cards and 3..5 board cards.");
            HandValue best = new HandValue(0);
            for (int a = 0; a < hole.Count - 1; a++) for (int b = a + 1; b < hole.Count; b++)
                for (int x = 0; x < board.Count - 2; x++) for (int y = x + 1; y < board.Count - 1; y++) for (int z = y + 1; z < board.Count; z++)
                {
                    var value = Five(new[] { hole[a], hole[b], board[x], board[y], board[z] });
                    if (value.Score > best.Score) best = value;
                }
            return best;
        }
    }
}
