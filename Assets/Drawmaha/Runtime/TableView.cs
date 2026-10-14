using System;
using System.Collections.Generic;
using System.Linq;
using Drawmaha.Network;
using UnityEngine;

namespace Drawmaha
{
    public sealed class TableView : MonoBehaviour
    {
        const float W = 1920, H = 1080;
        const float PokerTableScale = 0.82f;
        MultiplayerClient network;
        TableState previewTable;
        TableState CurrentTable { get { return previewTable ?? network.Table; } }
        Texture2D[] backgrounds, pokerTables, cardBacks;
        int backgroundIndex, tableIndex, cardBackIndex, cardFaceStyle, avatarIndex, holeCardOrderMode;
        const float CardDealSeconds = 0.14f, BoardDealSeconds = 0.38f, ChipFlightSeconds = 0.55f;
        readonly Color Accent = new Color(0.12f, 0.72f, 0.74f);
        readonly Color CardBackColor = new Color(0.075f, 0.24f, 0.38f);
        readonly Color CardFaceColor = new Color(0.98f, 0.97f, 0.92f);
        Texture2D white, round, rounded, felt, pokerChip, cardBack, buttonNormal, buttonHover, buttonPressed, accentNormal, accentHover, betSliderThumb;
        readonly Texture2D[] cardSuits = new Texture2D[4];
        readonly Texture2D[] cardSuitSources = new Texture2D[4];
        Font cardFont;
        bool ownsPokerChip;
        readonly bool[] selected = new bool[5];
        readonly int[] holeCardOrder = new int[5];
        readonly int[] chipFlightAmounts = new int[6];
        readonly float[] chipFlightStarted = new float[6];
        readonly Vector2[] positions = { new Vector2(960, 790), new Vector2(405, 565), new Vector2(405, 345), new Vector2(960, 195), new Vector2(1515, 345), new Vector2(1515, 565) };
        readonly Vector2[] betPositions = { new Vector2(960, 628), new Vector2(610, 604), new Vector2(610, 410), new Vector2(960, 327), new Vector2(1310, 410), new Vector2(1310, 604) };
        GUIStyle text, small, bold, title, button, accentButton, holeOrderTabButton, holeOrderTabAccentButton, input, betAmountStyle, betSliderStyle, betSliderThumbStyle;
        const string LocalhostEndpoint = "ws://127.0.0.1:8080/ws";
        const string AzureEndpoint = "wss://drawmaha.blackmushroom-85b81292.centralus.azurecontainerapps.io/ws";
        string endpoint = LocalhostEndpoint, username = "", invite = "", error = "", lastTable = "";
        bool post, showMenu, showHistory, showRules, showSettings;
        bool? optimisticDrawLocked, optimisticCheckFold, optimisticCallQueued;
        int optimisticCallAmount = -1;
        Vector2 historyScroll, customizationScroll;
        int lastHand = -1, shownBoard, lastActor = -99, customizationTab;
        float handStarted, nextBoardAt, raiseTo, errorUntil, leaveNoticeUntil, turnTimerStartedAt, turnTimerDuration;
        string leaveNotice = "", raiseAmountText = "", drawHandLabel = "", omahaHandLabel = "";
        TableState previous;
        Color Ink { get { return new Color(0.91f, 0.94f, 0.96f); } }
        Color Dim { get { return new Color(0.53f, 0.64f, 0.68f); } }
        Color Panel { get { return new Color(0.048f, 0.08f, 0.11f); } }
        Texture2D BackgroundImage { get { return backgrounds.Length > 0 ? backgrounds[Mathf.Clamp(backgroundIndex, 0, backgrounds.Length - 1)] : null; } }
        Texture2D PokerTableImage { get { return pokerTables.Length > 0 ? pokerTables[Mathf.Clamp(tableIndex, 0, pokerTables.Length - 1)] : null; } }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (UnityEngine.Object.FindFirstObjectByType<TableView>() != null) return;
            new GameObject("Drawmaha Multiplayer").AddComponent<TableView>();
            if (Camera.main == null)
            {
                var go = new GameObject("Main Camera"); go.tag = "MainCamera";
                var camera = go.AddComponent<Camera>(); camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.02f, 0.03f, 0.04f);
            }
        }
        void Awake()
        {
            Application.targetFrameRate = 60;
            Application.runInBackground = true;
            white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply();
            round = CircleTexture();
            betSliderThumb = CircleTexture(Accent);
            pokerChip = Resources.Load<Texture2D>("PokerChipStyles/GreenPokerChip");
            if (pokerChip == null)
            {
                pokerChip = PokerChipTexture();
                ownsPokerChip = true;
            }
            rounded = RoundedTexture(Color.white, Color.white);
            buttonNormal = RoundedTexture(new Color(0.10f, 0.24f, 0.40f), new Color(0.035f, 0.11f, 0.22f));
            buttonHover = RoundedTexture(new Color(0.16f, 0.37f, 0.55f), new Color(0.06f, 0.19f, 0.32f));
            buttonPressed = RoundedTexture(new Color(0.04f, 0.12f, 0.23f), new Color(0.02f, 0.07f, 0.14f));
            accentNormal = RoundedTexture(new Color(0.10f, 0.52f, 0.67f), new Color(0.035f, 0.28f, 0.43f));
            accentHover = RoundedTexture(new Color(0.17f, 0.65f, 0.78f), new Color(0.07f, 0.38f, 0.55f));
            backgrounds = Resources.LoadAll<Texture2D>("Backgrounds").OrderBy(image => image.name).ToArray();
            pokerTables = Resources.LoadAll<Texture2D>("PokerTables").OrderBy(image => image.name).ToArray();
            LoadCardStyleAssets();
            backgroundIndex = FindImageIndex(backgrounds, "drawmaha.background.asset", "BeachBackground");
            tableIndex = FindImageIndex(pokerTables, "drawmaha.pokertable.asset", "BeachPokerTable");
            cardBackIndex = FindImageIndex(cardBacks, "drawmaha.cardback.asset", "drawmaha_blue_card_back_720x1008");
            if (cardBacks.Length > 0) cardBack = cardBacks[cardBackIndex];
            cardFaceStyle = Mathf.Clamp(PlayerPrefs.GetInt("drawmaha.card.face.style", 0), 0, 2);
            holeCardOrderMode = Mathf.Clamp(PlayerPrefs.GetInt("drawmaha.hole.card.order", 0), 0, 2);
            PlayerPrefs.DeleteKey("drawmaha.endpoint"); username = PlayerPrefs.GetString("drawmaha.username", "");
            network = gameObject.AddComponent<MultiplayerClient>();
            network.StateChanged += Received;
            network.Failure += s => { ClearOptimisticPreferences(); error = s; errorUntil = Time.unscaledTime + 8; };
#if UNITY_EDITOR
            if (UnityEditor.EditorPrefs.GetBool("Drawmaha.VisualPreview", false))
            {
                UnityEditor.EditorPrefs.DeleteKey("Drawmaha.VisualPreview");
                previewTable = CreateVisualPreview();
                shownBoard = previewTable.board.Length;
                handStarted = Time.unscaledTime - InitialDuration(previewTable);
                turnTimerStartedAt = Time.unscaledTime;
                turnTimerDuration = Mathf.Max(0.1f, (previewTable.deadlineUnixMs - previewTable.serverUnixMs) / 1000f);
                UpdateHandLabels(previewTable);
            }
#endif
            RebuildFelt();
        }
        static int FindImageIndex(Texture2D[] options, string preferenceKey, string defaultName)
        {
            if (options.Length == 0) return 0;
            string preferredName = PlayerPrefs.GetString(preferenceKey, defaultName);
            int index = Array.FindIndex(options, image => image.name == preferredName);
            if (index < 0) index = Array.FindIndex(options, image => image.name == defaultName);
            return index < 0 ? 0 : index;
        }
        void OnDestroy()
        {
            Destroy(white); Destroy(round); Destroy(rounded); Destroy(felt);
            if (ownsPokerChip) Destroy(pokerChip);
            Destroy(buttonNormal); Destroy(buttonHover); Destroy(buttonPressed); Destroy(accentNormal); Destroy(accentHover); Destroy(betSliderThumb);
            foreach (Texture2D suit in cardSuits) if (suit != null) Destroy(suit);
        }
        void OnApplicationQuit()
        {
            if (previewTable == null && network != null && network.Connected && CurrentTable != null)
                network.SendBeforeQuit(new Command { type = "leave" });
        }
        void LoadCardStyleAssets()
        {
            cardBacks = Resources.LoadAll<Texture2D>("CardStyles/Backs").OrderBy(image => image.name).ToArray();
            cardFont = Resources.Load<Font>("Fonts/Bignoodletitling-8ojz");
            string[] suitNames = { "Club", "Diamond", "Heart", "Spade" };
            for (int suit = 0; suit < suitNames.Length; suit++)
            {
                Texture2D source = Resources.Load<Texture2D>("CardStyles/Suits/" + suitNames[suit]);
                if (source == null) continue;
                cardSuitSources[suit] = source;
                if (!source.isReadable) continue;
                const int outputSize = 384;
                var tintedPixels = new Color32[outputSize * outputSize];
                for (int y = 0; y < outputSize; y++) for (int x = 0; x < outputSize; x++)
                {
                    float u = (x + 0.5f) / outputSize, v = (y + 0.5f) / outputSize;
                    byte alpha = (byte)Mathf.RoundToInt(source.GetPixelBilinear(u, v).a * 255);
                    tintedPixels[y * outputSize + x] = new Color32(255, 255, 255, alpha);
                }
                Texture2D coloredSuit = new Texture2D(outputSize, outputSize, TextureFormat.RGBA32, true);
                coloredSuit.name = suitNames[suit] + "CardTint";
                coloredSuit.SetPixels32(tintedPixels);
                coloredSuit.Apply(true, false);
                coloredSuit.filterMode = FilterMode.Trilinear;
                coloredSuit.wrapMode = TextureWrapMode.Clamp;
                cardSuits[suit] = coloredSuit;
            }
        }
        void Received(TableState state)
        {
            if (state == null) return;
            UpdateHandLabels(state);
            if (previous != null && state.id == previous.id && state.hand == previous.hand)
                for (int i = 0; i < state.players.Length; i++)
                    if (previous.players[i].bet > 0 && (state.players[i].bet == 0 || (previous.stage == "Flop" && state.stage == "Draw")))
                    { chipFlightAmounts[i] = previous.players[i].bet; chipFlightStarted[i] = Time.unscaledTime; }
            bool wasLeaving = previous != null && previous.yourSeat >= 0 && previous.players[previous.yourSeat].leaving;
            bool isLeaving = state.yourSeat >= 0 && state.players[state.yourSeat].leaving;
            if (isLeaving && (previous == null || !wasLeaving))
            {
                leaveNotice = "You will leave after this hand.";
                leaveNoticeUntil = Time.unscaledTime + 3.5f;
            }
            if (state.id != lastTable || state.hand != lastHand)
            {
                lastTable = state.id; lastHand = state.hand; handStarted = Time.unscaledTime;
                ClearOptimisticPreferences();
                shownBoard = 0; nextBoardAt = handStarted + InitialDuration(state) + 0.1f;
                Array.Clear(selected, 0, 5); previous = null;
                Array.Clear(chipFlightAmounts, 0, chipFlightAmounts.Length);
            }
            if (previous != null && previous.stage == "Draw" && state.stage != "Draw") Array.Clear(selected, 0, selected.Length);
            if (previous != null && state.yourSeat >= 0 && state.yourSeat == previous.yourSeat
                && previous.players[state.yourSeat].drawLocked && !state.players[state.yourSeat].drawLocked)
                Array.Clear(selected, 0, selected.Length);
            if (state.stage == "Draw" && state.yourSeat >= 0 && state.yourSeat < state.players.Length
                && (state.players[state.yourSeat].status == "Stand pat" || state.players[state.yourSeat].status.StartsWith("Draw ", StringComparison.Ordinal)))
                Array.Clear(selected, 0, selected.Length);
            if (state.actor == state.yourSeat && (lastActor != state.actor || previous == null || previous.stage != state.stage))
                raiseTo = Math.Min(state.minRaise, state.maxRaise);
            if (state.actor != lastActor || previous == null || previous.stage != state.stage)
            {
                turnTimerStartedAt = Time.unscaledTime;
                turnTimerDuration = state.deadlineUnixMs > 0
                    ? Mathf.Max(0.1f, (state.deadlineUnixMs - state.serverUnixMs) / 1000f)
                    : 0;
            }
            lastActor = state.actor; previous = state;
        }
        void UpdateHandLabels(TableState state)
        {
            if (state.yourSeat < 0 || state.yourSeat >= state.players.Length)
            {
                drawHandLabel = omahaHandLabel = ""; return;
            }
            CardView[] cards = state.players[state.yourSeat].cards;
            if (cards == null || cards.Length != 5)
            {
                drawHandLabel = omahaHandLabel = ""; return;
            }
            var hole = cards.Select(card => new Drawmaha.Card(card.rank, card.suit)).ToArray();
            drawHandLabel = HandEvaluator.Five(hole).Name;
            omahaHandLabel = state.board != null && state.board.Length >= 3
                ? HandEvaluator.Omaha(hole, state.board.Select(card => new Drawmaha.Card(card.rank, card.suit)).ToArray()).Name
                : "Waiting for flop";
        }
        int BuildHoleCardOrder(CardView[] cards)
        {
            int count = Math.Min(cards == null ? 0 : cards.Length, holeCardOrder.Length);
            for (int i = 0; i < count; i++) holeCardOrder[i] = i;
            if (holeCardOrderMode == 0) return count;
            for (int i = 1; i < count; i++)
            {
                int cardIndex = holeCardOrder[i], insertAt = i;
                while (insertAt > 0 && CompareHoleCards(cards[cardIndex], cards[holeCardOrder[insertAt - 1]]) < 0)
                {
                    holeCardOrder[insertAt] = holeCardOrder[insertAt - 1];
                    insertAt--;
                }
                holeCardOrder[insertAt] = cardIndex;
            }
            return count;
        }
        int CompareHoleCards(CardView left, CardView right)
        {
            if (left == null) return right == null ? 0 : 1;
            if (right == null) return -1;
            if (holeCardOrderMode == 1)
            {
                int rankOrder = right.rank.CompareTo(left.rank);
                return rankOrder != 0 ? rankOrder : left.suit.CompareTo(right.suit);
            }
            int suitOrder = left.suit.CompareTo(right.suit);
            return suitOrder != 0 ? suitOrder : right.rank.CompareTo(left.rank);
        }
        float InitialDuration(TableState state) { return state.players.Count(p => p.inHand) * 5 * CardDealSeconds; }
        void Update()
        {
            var state = CurrentTable;
            if (state == null) return;
            if (shownBoard < state.board.Length && Time.unscaledTime >= nextBoardAt && Time.unscaledTime >= handStarted + InitialDuration(state))
            {
                shownBoard++; nextBoardAt = Time.unscaledTime + BoardDealSeconds;
            }
        }
        static TableState CreateVisualPreview()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var state = new TableState
            {
                id = "visual-preview", name = "Visual Preview", stage = "Preflop", yourSeat = 0,
                hand = 42, dealer = 2, smallBlindSeat = 3, bigBlindSeat = 4, smallBlind = 5,
                bigBlind = 10, pot = 185, actor = 0, call = 20, minRaise = 30, maxRaise = 400,
                revision = 1, serverUnixMs = now, deadlineUnixMs = now + 45000, showdown = false, canRaise = true,
                players = new SeatView[6], board = Array.Empty<CardView>(),
                results = new[] { "Visual preview hand" },
                history = new[] { "Hand 42 · Button at seat 3.", "Northstar calls 20.", "Visual preview state." }
            };
            string[] names = { "You", "Northstar", "M. Alvarez", "Dealer", "Kestrel", "RiverRun" };
            string[] actions = { "Draw 2", "Folded", "Small blind", "Raise to 125", "All in", "Check" };
            for (int i = 0; i < state.players.Length; i++)
            {
                state.players[i] = new SeatView
                {
                    seat = i, avatar = i, stack = i == 4 ? 0 : 850 + i * 175, bet = (i + 1) * 25,
                    count = 5, drawCount = i == 0 ? 2 : 0, username = names[i], status = actions[i], occupied = true,
                    folded = i == 1,
                    connected = true, inHand = true,
                    cards = Enumerable.Range(i * 5, 5).Select(PreviewCard).ToArray()
                };
            }
            return state;
        }
        static CardView PreviewCard(int index) { return new CardView { rank = 2 + index % 13, suit = index / 13 % 4 }; }
        Texture2D CircleTexture()
        {
            return CircleTexture(Color.white);
        }
        Texture2D CircleTexture(Color tint)
        {
            const int size = 512; float center = size / 2f;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true); var pixels = new Color[size * size];
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(center, center));
                Color pixel = tint; pixel.a *= Mathf.Clamp01(center + 0.5f - d); pixels[y * size + x] = pixel;
            }
            texture.SetPixels(pixels); texture.Apply(); texture.filterMode = FilterMode.Trilinear; texture.wrapMode = TextureWrapMode.Clamp; return texture;
        }
        Texture2D PokerChipTexture()
        {
            const int size = 1024; float center = (size - 1) * 0.5f, radius = size * 0.5f - 1;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];
            Color darkGreen = new Color(0.025f, 0.20f, 0.06f);
            Color green = new Color(0.035f, 0.58f, 0.12f);
            Color yellow = new Color(1f, 0.82f, 0.08f);
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - center, dy = y + 0.5f - center;
                float distance = Mathf.Sqrt(dx * dx + dy * dy), edge = distance / radius;
                float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                Color color;
                float nearestCardinal = Mathf.Min(Mathf.Abs(Mathf.DeltaAngle(angle, 0f)), Mathf.Abs(Mathf.DeltaAngle(angle, 90f)),
                    Mathf.Abs(Mathf.DeltaAngle(angle, 180f)), Mathf.Abs(Mathf.DeltaAngle(angle, -90f)));
                if (edge >= 0.97f) color = darkGreen;
                else if (edge >= 0.78f && nearestCardinal <= 8f) color = yellow;
                else color = Color.Lerp(darkGreen, green, 0.78f + 0.18f * (1f - dy / radius));
                color.a = Mathf.Clamp01((1f - edge) * radius + 0.5f);
                pixels[y * size + x] = color;
            }
            texture.SetPixels(pixels); texture.Apply(false, false); texture.filterMode = FilterMode.Bilinear; texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
        }
        Texture2D RoundedTexture(Color top, Color bottom)
        {
            const int width = 512, height = 256, radius = 16;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true);
            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                float cx = x < radius ? radius : x >= width - radius ? width - radius - 1 : x;
                float cy = y < radius ? radius : y >= height - radius ? height - radius - 1 : y;
                bool corner = (x < radius || x >= width - radius) && (y < radius || y >= height - radius);
                Color color = Color.Lerp(bottom, top, y / (float)(height - 1));
                if (corner) color.a = Mathf.Clamp01(radius + 0.5f - Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy)));
                pixels[y * width + x] = color;
            }
            texture.SetPixels(pixels); texture.Apply(); texture.filterMode = FilterMode.Trilinear; texture.wrapMode = TextureWrapMode.Clamp; return texture;
        }
        void RebuildFelt()
        {
            if (felt != null) Destroy(felt);
            const int width = 800, height = 420; felt = new Texture2D(width, height); var pixels = new Color[width * height];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                float nx = (x - width / 2f) / (width / 2f - 2), ny = (y - height / 2f) / (height / 2f - 2);
                float distance = Mathf.Sqrt(nx * nx + ny * ny); Color color = Color.clear;
                if (distance <= 1)
                {
                    if (distance > 0.92f) color = new Color(0.16f, 0.075f, 0.045f);
                    else if (distance > 0.909f) color = Accent * 0.7f;
                    else color = new Color(0.58f, 0.34f, 0.17f) * (1f - distance * 0.16f + ((x * 17 + y * 31) % 13) / 1100f);
                    color.a = 1;
                }
                pixels[y * width + x] = color;
            }
            felt.SetPixels(pixels); felt.Apply();
        }
        GUIStyle Style(int size, Color color, FontStyle weight = FontStyle.Normal, TextAnchor align = TextAnchor.UpperLeft, Font font = null)
        {
            var s = new GUIStyle(GUI.skin.label); s.fontSize = size; s.fontStyle = weight; s.normal.textColor = color;
            if (font != null) s.font = font;
            s.alignment = align; s.wordWrap = true; s.richText = false; s.padding = new RectOffset(0, 0, 0, 0); return s;
        }
        void Styles()
        {
            text = Style(20, Ink); small = Style(16, Dim); bold = Style(21, Ink, FontStyle.Bold); title = Style(38, Ink, FontStyle.Bold);
            button = new GUIStyle(GUI.skin.button); button.fontSize = 19; button.fontStyle = FontStyle.Bold; button.normal.textColor = Ink;
            button.hover.textColor = Color.white; button.active.textColor = Color.white; button.normal.background = buttonNormal;
            button.hover.background = buttonHover; button.active.background = buttonPressed; button.border = new RectOffset(16, 16, 16, 16);
            button.padding = new RectOffset(12, 12, 8, 8);
            accentButton = new GUIStyle(button); accentButton.normal.background = accentNormal; accentButton.hover.background = accentHover;
            accentButton.focused.background = accentNormal; accentButton.onNormal.background = accentNormal;
            accentButton.onHover.background = accentHover; accentButton.onActive.background = buttonPressed;
            accentButton.onFocused.background = accentNormal;
            accentButton.onNormal.textColor = Ink; accentButton.onHover.textColor = Color.white;
            accentButton.onActive.textColor = Color.white; accentButton.onFocused.textColor = Ink;
            holeOrderTabButton = new GUIStyle(button);
            holeOrderTabButton.fontSize = FitButtonFontSize(holeOrderTabButton, "Hole Card Order", 154);
            holeOrderTabAccentButton = new GUIStyle(accentButton) { fontSize = holeOrderTabButton.fontSize };
            input = new GUIStyle(GUI.skin.textField); input.fontSize = 19; input.padding = new RectOffset(12, 12, 10, 8);
            betAmountStyle = new GUIStyle(input); betAmountStyle.fontSize = 27; betAmountStyle.alignment = TextAnchor.MiddleCenter;
            betSliderStyle = new GUIStyle(GUI.skin.horizontalSlider);
            betSliderThumbStyle = new GUIStyle(GUI.skin.horizontalSliderThumb)
            {
                fixedWidth = 22, fixedHeight = 22, margin = new RectOffset(0, 0, 0, 0), padding = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(0, 0, 0, 0)
            };
            betSliderThumbStyle.normal.background = betSliderThumb;
            betSliderThumbStyle.hover.background = betSliderThumb;
            betSliderThumbStyle.active.background = betSliderThumb;
        }
        int FitButtonFontSize(GUIStyle style, string value, float width)
        {
            GUIStyle measuredStyle = new GUIStyle(style);
            while (measuredStyle.fontSize > 10 && measuredStyle.CalcSize(new GUIContent(value)).x > width)
                measuredStyle.fontSize--;
            return measuredStyle.fontSize;
        }
        void Fill(Rect rect, Color color, Texture2D texture = null)
        {
            Color old = GUI.color; GUI.color = color; GUI.DrawTexture(rect, texture ?? white, ScaleMode.StretchToFill, true); GUI.color = old;
        }
        void Label(float x, float y, float width, float height, string value, GUIStyle style = null) { GUI.Label(new Rect(x, y, width, height), value, style ?? text); }
        bool Button(Rect rect, string value, bool enabled = true, bool accent = false)
        {
            bool wasEnabled = GUI.enabled; GUI.enabled = wasEnabled && enabled;
            bool click = GUI.Button(rect, value, accent ? accentButton : button); GUI.enabled = wasEnabled; return click;
        }
        void OnGUI()
        {
            if (network == null) return; if (text == null) Styles();
            var currentTable = CurrentTable;
            float scale = Mathf.Min(Screen.width / W, Screen.height / H); Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(new Vector3((Screen.width - W * scale) / 2, (Screen.height - H * scale) / 2, 0), Quaternion.identity, new Vector3(scale, scale, 1));
            Fill(new Rect(0, 0, W, H), new Color(0.025f, 0.035f, 0.045f));
            if (BackgroundImage != null) GUI.DrawTexture(new Rect(0, 0, W, H), BackgroundImage, ScaleMode.ScaleAndCrop, true);
            bool needsRebuy = currentTable != null && currentTable.yourSeat >= 0 && currentTable.yourSeat < currentTable.players.Length
                && currentTable.players[currentTable.yourSeat].needsRebuy;
            bool wasEnabled = GUI.enabled; GUI.enabled = !showRules && !showHistory && !showSettings && !needsRebuy;
            Rect menuButton = new Rect(24, 24, 56, 56);
            if (GUI.Button(menuButton, new GUIContent("", showMenu ? "Close menu" : "Open menu"), button)) showMenu = !showMenu;
            Fill(new Rect(39, 39, 26, 3), Ink, white);
            Fill(new Rect(39, 50, 26, 3), Ink, white);
            Fill(new Rect(39, 61, 26, 3), Ink, white);
            if (!network.Connected && previewTable == null) ConnectScreen();
            else if (currentTable == null) Lobby();
            else Table();
            if (showMenu) Menu(currentTable);
            if (Time.unscaledTime < leaveNoticeUntil)
            {
                Rect banner = new Rect((W - 480) / 2, 24, 480, 52);
                Fill(banner, Panel, rounded);
                Fill(new Rect(banner.x, banner.y, 4, banner.height), Accent, white);
                Label(banner.x + 18, banner.y, banner.width - 30, banner.height, leaveNotice,
                    Style(17, Ink, FontStyle.Bold, TextAnchor.MiddleCenter));
            }
            Label(42, 1038, 1470, 25, Time.unscaledTime < errorUntil ? error : network.Status + (previewTable != null ? "  ·  Visual preview" : "  ·  Play chips"), small);
            GUI.enabled = wasEnabled;
            if (needsRebuy) RebuyDialog(currentTable);
            else { if (showRules) Rules(); if (showHistory) History(); if (showSettings) Settings(); }
            GUI.matrix = old;
        }
        void Menu(TableState currentTable)
        {
            bool atTable = currentTable != null && currentTable.yourSeat >= 0
                && currentTable.yourSeat < currentTable.players.Length;
            Fill(new Rect(24, 90, 270, atTable ? 250 : 138), Panel, rounded);
            if (Button(new Rect(38, 102, 242, 48), "Customization")) { showSettings = true; showMenu = false; }
            if (Button(new Rect(38, 158, 242, 48), "Rules")) { showRules = true; showMenu = false; }
            if (atTable)
            {
                if (Button(new Rect(38, 214, 242, 48), "History")) { showHistory = true; showMenu = false; }
                if (Button(new Rect(38, 270, 242, 48), "Leave Table"))
                {
                    network.Send(new Command { type = "leave" }); showMenu = false;
                }
            }
        }
        void RebuyDialog(TableState state)
        {
            Fill(new Rect(0, 0, W, H), new Color(0, 0, 0, 0.62f));
            Fill(new Rect(650, 390, 620, 300), Panel, rounded);
            Label(700, 425, 520, 48, "OUT OF CHIPS", Style(30, Ink, FontStyle.Bold, TextAnchor.MiddleCenter));
            Label(700, 483, 520, 42, "Rebuy for " + (state.bigBlind * 100).ToString("N0") + " chips?",
                Style(21, Dim, FontStyle.Normal, TextAnchor.MiddleCenter));
            bool connected = network.Connected;
            bool canRebuy = connected && !network.CommandPending;
            if (Button(new Rect(700, 555, 250, 58), "Leave Table", connected)) network.Send(new Command { type = "leave" });
            if (Button(new Rect(970, 555, 250, 58), "Rebuy", canRebuy, true)) network.Send(new Command { type = "rebuy" });
        }
        void Lobby()
        {
            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.matrix = oldMatrix * Matrix4x4.Translate(new Vector3(160, 40, 0));
            Fill(new Rect(250, 159, 1100, 751), Panel); Label(286, 187, 600, 45, "TAKE A SEAT", title);
            Label(286, 239, 960, 38, "Six seats per table. Hands begin automatically with two players.", small);
            Label(286, 294, 720, 28, "Entry preference:", small);
            if (Button(new Rect(470, 326, 227, 40), post ? "Post big blind ✓" : "Post big blind")) post = true;
            if (Button(new Rect(710, 326, 299, 40), post ? "Wait for big blind" : "Wait for big blind ✓")) post = false;
            Label(286, 392, 350, 26, "TABLE", small); Label(647, 392, 160, 26, "BLINDS", small);
            Label(832, 392, 180, 26, "BUY-IN", small); Label(1020, 392, 170, 26, "SEATS FILLED", small);
            var rows = network.Tables;
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i]; float y = 426 + i * 83;
                Fill(new Rect(276, y, 1044, 70), new Color(0.075f, 0.12f, 0.15f));
                Label(294, y + 20, 335, 35, row.name, bold); Label(649, y + 22, 160, 32, row.smallBlind + " / " + row.bigBlind);
                Label(834, y + 22, 170, 32, row.buyIn.ToString("N0")); Label(1020, y + 22, 145, 32, row.occupied + " / " + row.maxSeats);
                if (Button(new Rect(1180, y + 13, 121, 43), "Join", network.Connected && row.available > 0 && !network.CommandPending, true))
                    network.Send(new Command { type = "join", tableId = row.id, entryMode = post ? "post" : "wait" });
            }
            if (rows.Length == 0) Label(286, 456, 1000, 80, "No tables are available right now.", small);
            Label(286, 867, 1015, 30, "New arrivals join at a hand boundary. Posting is unavailable on the button or between the button and big blind.", Style(14, Dim));
            GUI.matrix = oldMatrix;
        }
        void ConnectScreen()
        {
            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.matrix = oldMatrix * Matrix4x4.Translate(new Vector3(160, 40, 0));
            Fill(new Rect(250, 159, 1100, 751), Panel);
            Label(286, 187, 600, 45, "WELCOME TO DRAWMAHA", title);
            Label(286, 239, 960, 38, "Enter your player details to find an open table.", small);
            Label(286, 321, 285, 26, "Username", small);
            username = GUI.TextField(new Rect(286, 354, 520, 47), username, 20, input);
            Label(286, 428, 285, 26, "Invite code", small);
            invite = GUI.PasswordField(new Rect(286, 461, 520, 47), invite, '*', 128, input);
            if (Button(new Rect(826, 461, 204, 47), "Avatar " + (avatarIndex + 1))) avatarIndex = (avatarIndex + 1) % 8;
            bool connecting = network.Status == "Connecting…";
            if (Button(new Rect(1050, 461, 262, 47), connecting ? "Connecting…" : "Connect", !connecting, true))
            {
                PlayerPrefs.SetString("drawmaha.username", username);
                PlayerPrefs.Save();
                network.Connect(endpoint, username, avatarIndex, invite);
            }
            if (Button(new Rect(1050, 522, 262, 33), "Forget saved identity", !connecting)) network.ForgetSession(endpoint, username);
            Label(286, 861, 900, 30, "Your invite code is used to join your private game server.", Style(14, Dim));
            GUI.matrix = oldMatrix;
        }
        void Table()
        {
            var t = CurrentTable;
            if (PokerTableImage != null)
            {
                float imageScale = Mathf.Min(W / PokerTableImage.width, H / PokerTableImage.height) * PokerTableScale;
                float imageWidth = PokerTableImage.width * imageScale;
                float imageHeight = PokerTableImage.height * imageScale;
                GUI.DrawTexture(new Rect((W - imageWidth) / 2, (H - imageHeight) / 2, imageWidth, imageHeight), PokerTableImage, ScaleMode.StretchToFill, true);
            }
            else Fill(new Rect(160, 245, 1600, 625), Color.white, felt);
            if (t.stage == "Waiting")
            {
                for (int i = 0; i < 6; i++) Seat(t, i);
                Label(560, 495, 800, 70, "Waiting for players", Style(30, Color.black, FontStyle.Bold, TextAnchor.MiddleCenter));
                Controls(t);
                return;
            }
            Rect potTracker = new Rect(840, 376, 240, 48);
            Fill(potTracker, new Color(0.30f, 0.32f, 0.34f, 0.96f), rounded);
            Label(potTracker.x + 8, potTracker.y, potTracker.width - 16, potTracker.height,
                "POT  " + t.pot.ToString("N0"), Style(23, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter));
            for (int i = 0; i < shownBoard; i++)
            {
                float elapsed = Time.unscaledTime - (nextBoardAt - BoardDealSeconds);
                Vector2 target = new Vector2(715 + i * 100, 430);
                Vector2 pos = i == shownBoard - 1 ? Vector2.Lerp(new Vector2(915, 360), target, Mathf.SmoothStep(0, 1, Mathf.Clamp01(elapsed / 0.2f))) : target;
                Card(new Rect(pos.x, pos.y, 90, 126), t.board[i], false);
            }
            for (int i = 0; i < 6; i++) Seat(t, i);
            if (t.stage == "Complete" && shownBoard == t.board.Length)
                Label(650, 568, 620, 86, string.Join("\n", t.results.Take(3).ToArray()), Style(17, Color.black, FontStyle.Normal, TextAnchor.MiddleCenter));
            Controls(t);
        }
        void Seat(TableState t, int actual)
        {
            int relative = (actual - Math.Max(0, t.yourSeat) + 6) % 6; Vector2 center = positions[relative]; var p = t.players[actual];
            Rect box = new Rect(center.x - 105, center.y, 210, 62);
            if (!p.occupied) return;
            bool own = actual == t.yourSeat, acting = actual == t.actor;
            DrawSeatPanel(p, box, acting);
            if (!p.inHand || p.folded || t.players.Count(player => player.inHand && !player.folded) <= 1)
            {
                DrawDealerButton(t, actual, box);
                DrawBetChip(p, relative, actual, t.stage);
                return;
            }
            int[] order = Enumerable.Range(1, 6).Select(n => (t.dealer + n + 6) % 6).Where(i => t.players[i].inHand).ToArray();
            int ordinal = Array.IndexOf(order, actual); int steps = Mathf.FloorToInt((Time.unscaledTime - handStarted) / CardDealSeconds) + 1;
            int visible = Math.Min(p.count, Mathf.Clamp((steps - ordinal + order.Length - 1) / Math.Max(1, order.Length), 0, 5));
            bool previewReveal = previewTable != null && (actual == 2 || actual == 4);
            bool reveal = own || previewReveal || (t.showdown && shownBoard == t.board.Length);
            int orderedCardCount = BuildHoleCardOrder(p.cards);
            float width = own ? 72 : 60, height = own ? 102 : 84, gap = width * 0.40f;
            int layoutCount = Mathf.Clamp(p.count, 1, 5); float middle = (layoutCount - 1) * 0.5f;
            float cardTop = center.y - height + 12;
            Rect[] rects = new Rect[visible]; float[] angles = new float[visible];
            for (int i = 0; i < visible; i++)
            {
                int cardIndex = i < orderedCardCount ? holeCardOrder[i] : i;
                float lift = own && selected[cardIndex] ? 18 : 0;
                Vector2 target = new Vector2(center.x - (gap * (layoutCount - 1) + width) / 2 + i * gap, cardTop - lift + Mathf.Abs(i - middle) * 1.25f);
                float age = Time.unscaledTime - handStarted - (i * order.Length + ordinal) * CardDealSeconds;
                Vector2 pos = Vector2.Lerp(new Vector2(915, 395), target, Mathf.SmoothStep(0, 1, Mathf.Clamp01(age / 0.2f)));
                rects[i] = new Rect(pos.x, pos.y, width, height); angles[i] = (i - middle) * 2f;
                Matrix4x4 saved = GUI.matrix; GUI.matrix = saved * RotationAtCanvasPivot(angles[i], new Vector2(pos.x + width / 2, pos.y + height));
                Card(rects[i], reveal && p.cards != null && cardIndex < p.cards.Length ? p.cards[cardIndex] : null,
                    own && selected[cardIndex]); GUI.matrix = saved;
            }
            DrawSeatPanel(p, box, acting);
            DrawDealerButton(t, actual, box);
            DrawBetChip(p, relative, actual, t.stage);
            if (own && !string.IsNullOrEmpty(drawHandLabel))
            {
                string omahaText = "Omaha hand: " + omahaHandLabel;
                string drawText = "Draw hand: " + drawHandLabel;
                GUIStyle handStyle = Style(15, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
                float badgeWidth = Mathf.Max(handStyle.CalcSize(new GUIContent(omahaText)).x,
                    handStyle.CalcSize(new GUIContent(drawText)).x) + 24;
                Rect badge = new Rect(box.center.x - badgeWidth / 2, 877, badgeWidth, 42);
                Color badgeColor = new Color(0.23f, 0.25f, 0.27f, 0.96f);
                Fill(new Rect(badge.x + badge.height / 2, badge.y, badge.width - badge.height, badge.height), badgeColor);
                Fill(new Rect(badge.x, badge.y, badge.height, badge.height), badgeColor, round);
                Fill(new Rect(badge.xMax - badge.height, badge.y, badge.height, badge.height), badgeColor, round);
                Label(badge.x + 8, badge.y + 1, badge.width - 16, 20, omahaText, handStyle);
                Label(badge.x + 8, badge.y + 21, badge.width - 16, 20, drawText, handStyle);
            }
            if (own && t.stage == "Draw" && !p.drawLocked && (previewTable != null || network.Connected) && !network.CommandPending && GUI.enabled && Time.unscaledTime >= handStarted + InitialDuration(t))
            {
                // Reverse hit order matches the visual stacking of overlapping cards.
                for (int i = visible - 1; i >= 0; i--)
                {
                    Matrix4x4 saved = GUI.matrix; GUI.matrix = saved * RotationAtCanvasPivot(angles[i], new Vector2(rects[i].x + width / 2, rects[i].y + height));
                    Color old = GUI.color; GUI.color = Color.clear;
                    if (GUI.Button(rects[i], GUIContent.none))
                    {
                        int cardIndex = i < orderedCardCount ? holeCardOrder[i] : i;
                        if (selected[cardIndex]) selected[cardIndex] = false;
                        else if (selected.Count(s => s) < 2) selected[cardIndex] = true;
                    }
                    GUI.color = old; GUI.matrix = saved;
                }
            }
        }
        void DrawSeatPanel(SeatView p, Rect box, bool acting)
        {
            Rect actionBox = new Rect(box.center.x - 64, box.yMax - 4, 128, 28);
            Rect avatarBox = new Rect(box.x + 8, box.y + 9, 44, 44);
            Fill(actionBox, new Color(0.045f, 0.105f, 0.145f, 0.98f), rounded);
            Fill(box, p.winner ? new Color(0.12f, 0.29f, 0.17f, 0.99f) : new Color(0.025f, 0.075f, 0.13f, 0.98f), rounded);
            Fill(new Rect(avatarBox.x - 2, avatarBox.y - 2, avatarBox.width + 4, avatarBox.height + 4), new Color(0.72f, 0.82f, 0.86f), round);
            Fill(avatarBox, AvatarColor(p.avatar), round);
            Label(avatarBox.x, avatarBox.y, avatarBox.width, avatarBox.height, AvatarInitials(p.username),
                Style(17, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter));
            GUIStyle nameStyle = Style(14, Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            nameStyle.wordWrap = false; nameStyle.clipping = TextClipping.Ellipsis;
            Label(avatarBox.xMax + 7, box.y + 6, box.xMax - avatarBox.xMax - 14, 23, p.username, nameStyle);
            Label(box.x, box.y + 33, box.width, 23, p.stack.ToString("N0") + " Chips",
                Style(17, Accent, FontStyle.Normal, TextAnchor.MiddleCenter));
            Label(actionBox.x + 4, actionBox.y, actionBox.width - 8, actionBox.height, PlayerAction(p),
                Style(13, p.winner ? new Color(0.52f, 0.95f, 0.57f) : Accent, FontStyle.Bold, TextAnchor.MiddleCenter));
            if (acting && turnTimerDuration > 0)
            {
                float remaining = Mathf.Clamp01(1f - (Time.unscaledTime - turnTimerStartedAt) / turnTimerDuration);
                DrawFuseOutline(box, remaining);
            }
        }
        void DrawFuseOutline(Rect box, float remaining)
        {
            Rect outline = new Rect(box.x - 1.5f, box.y - 1.5f, box.width + 3, box.height + 3);
            float topHalf = outline.width * 0.5f;
            float perimeter = 2 * (outline.width + outline.height);
            float elapsed = perimeter * (1f - remaining);
            const float thickness = 3f;
            float cursor = 0;
            DrawFuseHorizontal(outline.center.x, outline.y, topHalf, cursor, elapsed, false, thickness); cursor += topHalf;
            DrawFuseVertical(outline.xMax, outline.y, outline.height, cursor, elapsed, false, thickness); cursor += outline.height;
            DrawFuseHorizontal(outline.xMax, outline.yMax, outline.width, cursor, elapsed, true, thickness); cursor += outline.width;
            DrawFuseVertical(outline.x, outline.yMax, outline.height, cursor, elapsed, true, thickness); cursor += outline.height;
            DrawFuseHorizontal(outline.center.x, outline.y, topHalf, cursor, elapsed, true, thickness);
        }
        void DrawFuseHorizontal(float pathX, float y, float length, float segmentStart, float elapsed, bool reverse, float thickness)
        {
            float offset = Mathf.Clamp(elapsed - segmentStart, 0, length);
            float visibleLength = length - offset;
            if (visibleLength <= 0) return;
            float x = reverse ? pathX - offset - visibleLength : pathX + offset;
            Fill(new Rect(x, y - thickness * 0.5f, visibleLength, thickness), Accent, white);
        }
        void DrawFuseVertical(float x, float pathY, float length, float segmentStart, float elapsed, bool reverse, float thickness)
        {
            float offset = Mathf.Clamp(elapsed - segmentStart, 0, length);
            float visibleLength = length - offset;
            if (visibleLength <= 0) return;
            float y = reverse ? pathY - offset - visibleLength : pathY + offset;
            Fill(new Rect(x - thickness * 0.5f, y, thickness, visibleLength), Accent, white);
        }
        static string PlayerAction(SeatView player)
        {
            if (player.winner) return "WINNER";
            if (player.drawLocked) return player.status == "Stand pat queued" ? "Pat queued" : "Draw queued";
            string status = player.status ?? "";
            if (status == "Auto-check" || status == "Auto-fold" || status == "Auto-call") return status;
            if (status == "Waiting for big blind") return "Wait for BB";
            if (status == "Post next eligible hand") return "Post next";
            if (status == "Ready for next hand") return "Next hand";
            if (player.folded || status == "Folded") return "Fold";
            if (status == "All in" || (player.inHand && player.stack == 0)) return "All-in";
            if (player.drawCount > 0) return "Draws " + player.drawCount;
            if (status == "Stand pat") return "Pats";
            if (status.StartsWith("Draw ", StringComparison.Ordinal)) return "Draws " + status.Substring(5);
            if (status == "Small blind" || status == "Big blind" || status == "Entry big blind") return "Bet";
            if (status.StartsWith("Raise to ", StringComparison.Ordinal)) return "Raise " + status.Substring(9);
            if (status == "Check" || status.StartsWith("Call ", StringComparison.Ordinal)) return status;
            return "";
        }
        static string AvatarInitials(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return "?";
            string[] parts = username.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1) return (parts[0][0].ToString() + parts[parts.Length - 1][0]).ToUpperInvariant();
            return parts[0].Substring(0, 1).ToUpperInvariant();
        }
        static Color AvatarColor(int avatar)
        {
            switch (Mathf.Clamp(avatar, 0, 7))
            {
                case 0: return new Color(0.17f, 0.57f, 0.78f);
                case 1: return new Color(0.87f, 0.31f, 0.26f);
                case 2: return new Color(0.22f, 0.62f, 0.41f);
                case 3: return new Color(0.86f, 0.62f, 0.16f);
                case 4: return new Color(0.54f, 0.39f, 0.76f);
                case 5: return new Color(0.13f, 0.62f, 0.61f);
                case 6: return new Color(0.80f, 0.34f, 0.55f);
                default: return new Color(0.42f, 0.55f, 0.65f);
            }
        }
        void DrawDealerButton(TableState t, int actual, Rect box)
        {
            if (t.hand <= 0 || actual != t.dealer) return;
            Fill(new Rect(box.x + box.width - 20, box.y - 12, 26, 26), Accent, round);
            Label(box.x + box.width - 20, box.y - 11, 26, 23, "D", Style(13, Panel, FontStyle.Bold, TextAnchor.MiddleCenter));
        }
        void DrawBetChip(SeatView player, int relative, int actual, string stage)
        {
            Vector2 center;
            int amount;
            float alpha = 1;
            if (stage == "Draw" && chipFlightAmounts[actual] <= 0) return;
            if (stage != "Draw" && player.bet > 0) { center = betPositions[relative]; amount = player.bet; }
            else
            {
                amount = chipFlightAmounts[actual];
                float progress = (Time.unscaledTime - chipFlightStarted[actual]) / ChipFlightSeconds;
                if (amount <= 0 || progress >= 1) { chipFlightAmounts[actual] = 0; return; }
                progress = Mathf.Clamp01(progress);
                center = Vector2.Lerp(betPositions[relative], new Vector2(960, 386), Mathf.SmoothStep(0, 1, progress));
                alpha = 1 - progress;
            }
            Rect chip = new Rect(center.x - 23, center.y - 23, 46, 46);
            Fill(chip, new Color(1, 1, 1, alpha), pokerChip);
            Label(chip.x - 15, chip.yMax + 1, chip.width + 30, 21, amount.ToString("N0"), Style(15, new Color(0.035f, 0.07f, 0.045f, alpha), FontStyle.Bold, TextAnchor.MiddleCenter));
        }
        static Matrix4x4 RotationAtCanvasPivot(float angle, Vector2 pivot)
        {
            Vector3 point = new Vector3(pivot.x, pivot.y, 0);
            return Matrix4x4.Translate(point) * Matrix4x4.Rotate(Quaternion.Euler(0, 0, angle)) * Matrix4x4.Translate(-point);
        }
        void Card(Rect rect, CardView face, bool marked)
        {
            float cardScale = rect.width / 90f;
            Fill(new Rect(rect.x + 2 * cardScale, rect.y + 5 * cardScale, rect.width, rect.height), new Color(0, 0, 0, 0.28f), rounded);
            if (face == null)
            {
                Fill(new Rect(rect.x - 1.5f * cardScale, rect.y - 1.5f * cardScale, rect.width + 3 * cardScale, rect.height + 3 * cardScale), new Color(0.82f, 0.84f, 0.83f), rounded);
                Fill(rect, CardBackColor, rounded);
                if (cardBack != null) GUI.DrawTexture(rect, cardBack, ScaleMode.StretchToFill, true);
                else
                {
                    Fill(new Rect(rect.x + 5, rect.y + 5, rect.width - 10, rect.height - 10), new Color(0.24f, 0.52f, 0.64f), rounded);
                    Fill(new Rect(rect.x + 8, rect.y + 8, rect.width - 16, rect.height - 16), CardBackColor, rounded);
                    Fill(new Rect(rect.x + rect.width * 0.32f, rect.y + rect.height * 0.32f, rect.width * 0.36f, rect.height * 0.36f), Accent, round);
                    Label(rect.x, rect.y + rect.height / 2 - 20, rect.width, 40, "D", Style(28, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter));
                }
                return;
            }
            DrawCardFace(rect, face, marked, cardFaceStyle);
        }
        void DrawCardFace(Rect rect, CardView face, bool marked, int styleIndex)
        {
            float cardScale = rect.width / 90f;
            var value = new Drawmaha.Card(face.rank, face.suit);
            Color suitColor = SuitColor(face.suit, styleIndex);
            bool coloredFace = styleIndex == 2;
            Color edgeColor = marked ? Accent : coloredFace ? suitColor : new Color(0.82f, 0.84f, 0.83f);
            Fill(new Rect(rect.x - 1.5f * cardScale, rect.y - 1.5f * cardScale, rect.width + 3 * cardScale, rect.height + 3 * cardScale), edgeColor, rounded);
            Fill(rect, coloredFace ? suitColor : CardFaceColor, rounded);
            float standardCornerWidth = 40f * cardScale;
            float cornerWidth = (value.RankText == "10" ? 62f : 40f) * cardScale;
            int cornerRankSize = Mathf.RoundToInt(62f * cardScale);
            float cornerRankHeight = rect.height * 0.46f;
            float cornerIndexX = rect.x + 2 * cardScale;
            float cornerRankX = cornerIndexX - (cornerWidth - standardCornerWidth) * 0.5f;
            float cornerIndexCenter = cornerIndexX + standardCornerWidth / 2;
            Label(cornerRankX, rect.y - 3 * cardScale, cornerWidth, cornerRankHeight, value.RankText,
                Style(cornerRankSize, coloredFace ? Color.white : suitColor, FontStyle.Normal, TextAnchor.MiddleCenter, cardFont));
            float cornerSuitSize = 38f * cardScale;
            DrawSuit(new Rect(cornerIndexCenter - cornerSuitSize / 2, rect.y + rect.height * 0.40f,
                cornerSuitSize, cornerSuitSize),
                face.suit, coloredFace ? Color.white : suitColor);
            float centerSuitSize = 70f * cardScale;
            Rect largeSuit = new Rect(rect.xMax - centerSuitSize + 6 * cardScale,
                rect.yMax - centerSuitSize + 5 * cardScale, centerSuitSize, centerSuitSize);
            DrawSuit(largeSuit, face.suit, coloredFace ? Color.white : suitColor);
            if (marked)
            {
                float bannerHeight = 19 * cardScale;
                Fill(new Rect(rect.x, rect.y + rect.height - bannerHeight, rect.width, bannerHeight), Accent, rounded);
                Label(rect.x, rect.y + rect.height - bannerHeight, rect.width, bannerHeight, "DISCARD",
                    Style(Mathf.RoundToInt(11 * cardScale), Panel, FontStyle.Bold, TextAnchor.MiddleCenter));
            }
        }
        static Color SuitColor(int suit, int styleIndex)
        {
            if (styleIndex == 1 || styleIndex == 2)
            {
                switch (suit)
                {
                    case 0: return new Color(0.10f, 0.56f, 0.26f);
                    case 1: return new Color(0.12f, 0.39f, 0.86f);
                    case 2: return new Color(0.84f, 0.12f, 0.16f);
                    default: return new Color(0.08f, 0.09f, 0.11f);
                }
            }
            switch (suit)
            {
                case 1:
                case 2: return new Color(0.76f, 0.08f, 0.13f);
                default: return new Color(0.08f, 0.09f, 0.11f);
            }
        }
        void DrawSuit(Rect rect, int suit, Color tint)
        {
            if (suit >= 0 && suit < cardSuits.Length && cardSuits[suit] != null)
                Fill(rect, tint, cardSuits[suit]);
            else if (suit >= 0 && suit < cardSuitSources.Length && cardSuitSources[suit] != null)
                Fill(rect, tint, cardSuitSources[suit]);
        }
        void Controls(TableState t)
        {
            Fill(new Rect(1230, 880, 650, 184), Panel, rounded);
            var own = t.players[t.yourSeat];
            if (t.actor == t.yourSeat || !IsBettingStage(t.stage))
            {
                optimisticCheckFold = null; optimisticCallQueued = null; optimisticCallAmount = -1;
            }
            if (t.stage != "Draw") optimisticDrawLocked = null;
            bool animationsDone = Time.unscaledTime >= handStarted + InitialDuration(t) && shownBoard == t.board.Length;
            bool enabled = network.Connected && !network.CommandPending && animationsDone;
            bool preferenceEnabled = network.Connected && !network.CommandPending;
            bool bettingPreferenceEnabled = preferenceEnabled && network.SupportsWaitingPreferences;
            if (!own.inHand || t.stage == "Waiting")
            {
                Label(1254, 894, 580, 29, own.status, small);
                if (Button(new Rect(1250, 966, 275, 52), "Post big blind", enabled)) network.Send(new Command { type = "entry", entryMode = "post" });
                if (Button(new Rect(1537, 966, 318, 52), "Wait for big blind", enabled)) network.Send(new Command { type = "entry", entryMode = "wait" }); return;
            }
            if (t.stage == "Complete") return;
            if (t.stage == "Draw")
            {
                int count = selected.Count(s => s); bool acting = t.actor == t.yourSeat;
                bool nextDrawLocked = false;
                bool drawLocked = CurrentPreference(ref optimisticDrawLocked, own.drawLocked);
                if (drawLocked)
                {
                    int queuedCount = own.drawLocked ? own.queuedDrawCount : count;
                    string lockedChoice = queuedCount == 0 ? "Stand pat is locked" : "Draw " + queuedCount + " is locked";
                    Label(1254, 901, 570, 45, lockedChoice, small);
                    if (!acting && PreferenceButton(new Rect(1250, 966, 605, 52), queuedCount == 0 ? "Stand Pat" : "Draw " + queuedCount, true, preferenceEnabled, out nextDrawLocked))
                    {
                        optimisticDrawLocked = nextDrawLocked;
                        network.Send(new Command { type = "lockDraw", discards = Enumerable.Range(0, 5).Where(i => selected[i]).ToArray() });
                    }
                    return;
                }
                if (own.status == "Stand pat" || own.status.StartsWith("Draw ", StringComparison.Ordinal))
                {
                    Label(1254, 925, 580, 44, "Draw complete", small); return;
                }
                Label(1254, 901, 570, 45, acting ? "Select up to two cards above.  " + count + "/2 selected." : "Select discards now, then lock your choice.  " + count + "/2 selected.", small);
                string drawLabel = count == 0 ? "Stand pat" : "Draw " + count + " cards";
                bool drawPressed = acting
                    ? Button(new Rect(1250, 966, 605, 52), drawLabel, enabled, true)
                    : PreferenceButton(new Rect(1250, 966, 605, 52), drawLabel, false, preferenceEnabled, out nextDrawLocked);
                if (drawPressed)
                {
                    if (acting) network.Send(new Command { type = "draw", discards = Enumerable.Range(0, 5).Where(i => selected[i]).ToArray() });
                    else
                    {
                        optimisticDrawLocked = nextDrawLocked;
                        network.Send(new Command { type = "lockDraw", discards = Enumerable.Range(0, 5).Where(i => selected[i]).ToArray() });
                    }
                    if (acting) Array.Clear(selected, 0, selected.Length);
                }
                return;
            }
            if (t.actor != t.yourSeat || own.folded)
            {
                string waitingMessage = !network.SupportsWaitingPreferences ? "Server update required for waiting-action preferences"
                    : own.checkFold ? "Check/Fold queued · waiting for your turn"
                    : own.callQueued ? "Auto-call queued · waiting for your turn"
                    : animationsDone ? "Waiting for the other players…" : "Dealing cards…";
                Label(1254, 901, 580, 44, waitingMessage, small);
                if (t.actor != t.yourSeat && own.waitingToAct && own.inHand && !own.folded && own.stack > 0 && IsBettingStage(t.stage))
                {
                    bool checkFold = CurrentPreference(ref optimisticCheckFold, own.checkFold);
                    if (t.call == 0 && PreferenceButton(new Rect(1250, 966, 282, 56), "Check/Fold", checkFold, bettingPreferenceEnabled, out bool nextCheckFold))
                    {
                        optimisticCheckFold = nextCheckFold;
                        network.Send(new Command { type = "checkFold", enabled = nextCheckFold });
                    }
                    bool callQueued = CurrentCallPreference(own.callQueued, t.call);
                    if (t.call > 0 && PreferenceButton(new Rect(1540, 966, 315, 56), "Call " + t.call.ToString("N0"), callQueued, bettingPreferenceEnabled, out bool nextCallQueued))
                    {
                        optimisticCallQueued = nextCallQueued;
                        optimisticCallAmount = t.call;
                        network.Send(new Command { type = "autoCall", enabled = nextCallQueued, callAmount = t.call });
                    }
                }
                return;
            }
            bool callAllIn = t.call > 0 && t.call >= own.stack;
            if (callAllIn)
            {
                Label(1254, 900, 580, 48, "Calling commits your remaining stack.", small);
                if (Button(new Rect(1250, 966, 294, 56), "Fold", enabled)) network.Send(new Command { type = "fold" });
                if (Button(new Rect(1560, 966, 295, 56), "All-in", enabled, true)) network.Send(new Command { type = "call" });
                return;
            }
            int min = Math.Min(t.minRaise, t.maxRaise), max = t.maxRaise;
            if (t.canRaise)
            {
                raiseTo = Mathf.Clamp(raiseTo, min, max);
                int presetWidth = 145;
                if (Button(new Rect(1250, 886, presetWidth, 40), "2x", enabled)) raiseTo = RaisePreset(own, t, 2, min, max);
                if (Button(new Rect(1403, 886, presetWidth, 40), "3x", enabled)) raiseTo = RaisePreset(own, t, 3, min, max);
                if (Button(new Rect(1556, 886, presetWidth, 40), "4x", enabled)) raiseTo = RaisePreset(own, t, 4, min, max);
                if (Button(new Rect(1709, 886, 146, 40), "POT", enabled)) raiseTo = max;
                int step = Math.Max(1, (max - min) / 20);
                if (Button(new Rect(1250, 934, 40, 42), "-", enabled)) raiseTo = Mathf.Max(min, raiseTo - step);
                Rect sliderRect = new Rect(1290, 947, 314, 16);
                raiseTo = Mathf.Clamp(GUI.HorizontalSlider(sliderRect, raiseTo, min, Math.Max(min + 1, max), betSliderStyle, betSliderThumbStyle), min, max);
                if (Button(new Rect(1604, 934, 40, 42), "+", enabled)) raiseTo = Mathf.Min(max, raiseTo + step);
                DrawRaiseAmount(new Rect(1653, 930, 202, 50), min, max);
            }
            if (t.canRaise)
            {
                bool raiseAllIn = Mathf.RoundToInt(raiseTo) == own.bet + own.stack;
                bool isBet = t.stage != "Preflop" && t.call == 0;
                string raiseLabel = raiseAllIn ? "All-in" : (isBet ? "Bet " : "Raise ") + Mathf.RoundToInt(raiseTo).ToString("N0");
                if (Button(new Rect(1250, 990, 194, 56), "Fold", enabled)) network.Send(new Command { type = "fold" });
                if (Button(new Rect(1455, 990, 194, 56), t.call == 0 ? "Check" : "Call " + t.call.ToString("N0"), enabled, true)) network.Send(new Command { type = "call" });
                if (Button(new Rect(1660, 990, 195, 56), raiseLabel, enabled, true)) network.Send(new Command { type = "raise", raiseTo = Mathf.RoundToInt(raiseTo) });
            }
            else
            {
                if (Button(new Rect(1250, 990, 294, 56), "Fold", enabled)) network.Send(new Command { type = "fold" });
                if (Button(new Rect(1560, 990, 295, 56), t.call == 0 ? "Check" : "Call " + t.call.ToString("N0"), enabled, true)) network.Send(new Command { type = "call" });
            }
        }
        static int RaisePreset(SeatView player, TableState table, int multiple, int min, int max)
        {
            int reference = Math.Max(table.bigBlind, player.bet + table.call);
            return Mathf.Clamp(reference * multiple, min, max);
        }
        bool CurrentPreference(ref bool? optimisticValue, bool authoritativeValue)
        {
            if (!optimisticValue.HasValue) return authoritativeValue;
            if (optimisticValue.Value == authoritativeValue || !network.Connected)
            {
                optimisticValue = null;
                return authoritativeValue;
            }
            return optimisticValue.Value;
        }
        bool CurrentCallPreference(bool authoritativeValue, int callAmount)
        {
            if (!optimisticCallQueued.HasValue) return authoritativeValue;
            if (callAmount != optimisticCallAmount || optimisticCallQueued.Value == authoritativeValue || !network.Connected)
            {
                optimisticCallQueued = null;
                optimisticCallAmount = -1;
                return authoritativeValue;
            }
            return optimisticCallQueued.Value;
        }
        void ClearOptimisticPreferences()
        {
            optimisticDrawLocked = null; optimisticCheckFold = null; optimisticCallQueued = null;
            optimisticCallAmount = -1;
        }
        bool PreferenceButton(Rect rect, string label, bool isChecked, bool enabled, out bool nextChecked)
        {
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && enabled;
            nextChecked = GUI.Toggle(rect, isChecked, GUIContent.none, accentButton);
            GUI.enabled = wasEnabled;
            Rect check = new Rect(rect.x + 14, rect.center.y - 12, 24, 24);
            Fill(new Rect(check.x - 2, check.y - 2, check.width + 4, check.height + 4), new Color(0.72f, 0.78f, 0.80f));
            Fill(check, nextChecked ? Accent : new Color(0.025f, 0.045f, 0.065f));
            if (nextChecked) Label(check.x, check.y - 1, check.width, check.height, "✓", Style(18, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter));
            Label(rect.x + 48, rect.y, rect.width - 56, rect.height, label, Style(17, Ink, FontStyle.Bold, TextAnchor.MiddleCenter));
            return nextChecked != isChecked;
        }
        static bool IsBettingStage(string stage)
        {
            return stage == "Preflop" || stage == "Flop" || stage == "Turn" || stage == "River";
        }
        void DrawRaiseAmount(Rect rect, int min, int max)
        {
            const string controlName = "DrawmahaRaiseAmount";
            bool focusedBefore = GUI.GetNameOfFocusedControl() == controlName;
            if (!focusedBefore) raiseAmountText = Mathf.RoundToInt(raiseTo).ToString();
            bool clickAway = focusedBefore && Event.current.type == EventType.MouseDown && !rect.Contains(Event.current.mousePosition);
            Fill(rect, new Color(0.025f, 0.045f, 0.065f), rounded);
            GUI.SetNextControlName(controlName);
            raiseAmountText = GUI.TextField(rect, raiseAmountText, 16, betAmountStyle);
            bool focusedAfter = GUI.GetNameOfFocusedControl() == controlName;
            bool enter = focusedAfter && Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if (!clickAway && !enter && !(focusedBefore && !focusedAfter)) return;

            string enteredText = raiseAmountText.Trim();
            long entered;
            if (long.TryParse(enteredText, out entered))
                raiseTo = entered < min ? min : entered > max ? max : (int)entered;
            else if (IsIntegerText(enteredText))
                raiseTo = enteredText.StartsWith("-", StringComparison.Ordinal) ? min : max;
            else
                raiseTo = Mathf.Clamp(raiseTo, min, max);

            raiseAmountText = Mathf.RoundToInt(raiseTo).ToString();
            GUI.FocusControl(null);
            if (enter) Event.current.Use();
        }
        static bool IsIntegerText(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            int start = value[0] == '-' || value[0] == '+' ? 1 : 0;
            return start < value.Length && value.Skip(start).All(char.IsDigit);
        }
        void Overlay(string heading)
        {
            Fill(new Rect(330, 140, 940, 720), new Color(0.20f, 0.21f, 0.22f, 0.72f));
            Fill(new Rect(350, 160, 900, 680), Panel); Label(394, 193, 805, 55, heading, title);
        }
        void Rules()
        {
            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.matrix = oldMatrix * Matrix4x4.Translate(new Vector3(160, 40, 0));
            Overlay("TWO HANDS. ONE POT.");
            Label(394, 270, 805, 475,
                "Five private cards. Pot-limit betting before the flop, after the flop, after the turn, and after the river.\n\n" +
                "One draw of 0–2 cards AFTER flop betting. Replacements arrive immediately. The final cards count for BOTH halves.\n\n" +
                "OMAHA HIGH: exactly two private cards and three board cards.\n\nDRAW HIGH: all five final private cards.\n\n" +
                "Side pots are scored separately. Ties split each half. The Omaha half gets the odd chip; tied odd chips go clockwise from the button.\n\n" +
                "TABLE ENTRY: post a big blind in the next eligible position, or wait for your natural big blind. Never enter an already dealt hand. Entry is blocked on the button and between the button and big blind.\n\n" +
                "Turn timeout: check if free, otherwise fold; stand pat during the draw. Your seat is reserved for 90 seconds if disconnected.", text);
            if (Button(new Rect(964, 773, 241, 44), "Back to game", true, true)) showRules = false;
            GUI.matrix = oldMatrix;
        }
        void History()
        {
            Matrix4x4 oldMatrix = GUI.matrix;
            var state = CurrentTable;
            Fill(new Rect(0, 0, W, H), new Color(0, 0, 0, 0.42f));
            Rect panel = new Rect(32, 32, 573, 1016);
            Event currentEvent = Event.current;
            if (currentEvent.type == EventType.MouseDown && !panel.Contains(currentEvent.mousePosition))
            {
                showHistory = false;
                currentEvent.Use();
                GUI.matrix = oldMatrix;
                return;
            }
            Fill(panel, Panel, rounded);
            Fill(new Rect(panel.x, panel.y, 5, panel.height), Accent, white);
            Label(panel.x + 24, panel.y + 18, panel.width - 92, 44, "HAND HISTORY", Style(28, Ink, FontStyle.Bold));
            if (Button(new Rect(panel.xMax - 58, panel.y + 14, 40, 40), "X", true, true)) showHistory = false;
            HandHistoryView[] hands = state?.handHistory ?? Array.Empty<HandHistoryView>();
            Label(panel.x + 24, panel.y + 60, panel.width - 48, 24, hands.Length == 0 ? "No completed hands" : "Latest " + hands.Length + " hands", small);
            Rect viewport = new Rect(panel.x + 16, panel.y + 96, panel.width - 32, panel.height - 170);
            float contentHeight = 0;
            foreach (HandHistoryView hand in hands)
                contentHeight += 160 + (hand.players?.Length ?? 0) * 76 + 12;
            historyScroll = GUI.BeginScrollView(viewport, historyScroll,
                new Rect(0, 0, viewport.width - 18, Mathf.Max(viewport.height, contentHeight)));
            float y = 0;
            foreach (HandHistoryView hand in hands)
            {
                float entryHeight = 160 + (hand.players?.Length ?? 0) * 76;
                if (y + entryHeight < historyScroll.y || y > historyScroll.y + viewport.height)
                {
                    y += entryHeight + 12;
                    continue;
                }
                Rect entry = new Rect(0, y, viewport.width - 22, entryHeight);
                Fill(entry, new Color(0.065f, 0.105f, 0.13f, 0.96f), rounded);
                Label(entry.x + 12, entry.y + 8, entry.width - 24, 24,
                    "HAND #" + hand.hand,
                    Style(18, Ink, FontStyle.Bold));
                const float cardWidth = 48, cardHeight = 68, cardGap = 4, cardColumnOffset = 158;
                CardView[] board = hand.board ?? Array.Empty<CardView>();
                float boardSpan = board.Length == 0 ? 0 : board.Length * cardWidth + (board.Length - 1) * cardGap;
                float boardY = entry.y + 57;
                Label(entry.x + 12, boardY, 142, cardHeight, "BOARD",
                    Style(15, Dim, FontStyle.Bold, TextAnchor.MiddleLeft));
                if (board.Length > 0)
                    Fill(new Rect(entry.x + cardColumnOffset - 6, boardY - 4, boardSpan + 12, cardHeight + 10),
                        new Color(0.08f, 0.34f, 0.52f, 0.72f), rounded);
                for (int i = 0; i < board.Length; i++)
                    DrawCardFace(new Rect(entry.x + cardColumnOffset + i * (cardWidth + cardGap), boardY,
                        cardWidth, cardHeight), board[i], false, cardFaceStyle);
                float playerY = entry.y + 132;
                foreach (HandHistoryPlayerView player in hand.players ?? Array.Empty<HandHistoryPlayerView>())
                {
                    Rect avatar = new Rect(entry.x + 12, playerY + 13, 42, 42);
                    Fill(new Rect(avatar.x - 2, avatar.y - 2, avatar.width + 4, avatar.height + 4),
                        new Color(0.72f, 0.82f, 0.86f), round);
                    Fill(avatar, AvatarColor(player.avatar), round);
                    Label(avatar.x, avatar.y, avatar.width, avatar.height, AvatarInitials(player.username),
                        Style(16, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter));
                    GUIStyle playerNameStyle = Style(16, Ink, FontStyle.Bold);
                    playerNameStyle.wordWrap = false; playerNameStyle.clipping = TextClipping.Ellipsis;
                    playerNameStyle.alignment = TextAnchor.MiddleLeft;
                    Label(entry.x + 60, playerY + 8, 92, 25, player.username, playerNameStyle);
                    string playerState = player.folded ? "Folded" : player.revealed ? "Revealed" : "Not revealed";
                    Label(entry.x + 60, playerY + 34, 92, 22, playerState, Style(14, player.folded ? Dim : Accent));
                    if (player.revealed && player.cards != null)
                    {
                        int orderedCardCount = BuildHoleCardOrder(player.cards);
                        for (int i = 0; i < orderedCardCount; i++)
                            DrawCardFace(new Rect(entry.x + cardColumnOffset + i * (cardWidth + cardGap), playerY + 1,
                                cardWidth, cardHeight), player.cards[holeCardOrder[i]], false, cardFaceStyle);
                    }
                    playerY += 76;
                }
                y += entryHeight + 12;
            }
            GUI.EndScrollView();
            if (Button(new Rect(panel.x + 18, panel.yMax - 58, panel.width - 36, 42), "Back to game", true, true)) showHistory = false;
            GUI.matrix = oldMatrix;
        }
        void Settings()
        {
            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.matrix = oldMatrix * Matrix4x4.Translate(new Vector3(160, 40, 0));
            Overlay("CUSTOMIZATION");
            string[] tabs = { "Card Face", "Card Back", "Table", "Background", "Hole Card Order" };
            const float tabWidth = 154, tabGap = 8;
            for (int i = 0; i < tabs.Length; i++)
            {
                Rect tab = new Rect(394 + i * (tabWidth + tabGap), 258, tabWidth, 48);
                bool selectedTab = customizationTab == i;
                bool clicked;
                if (i == 4)
                {
                    bool wasEnabled = GUI.enabled;
                    GUI.enabled = wasEnabled;
                    clicked = GUI.Button(tab, tabs[i], selectedTab ? holeOrderTabAccentButton : holeOrderTabButton);
                    GUI.enabled = wasEnabled;
                }
                else clicked = Button(tab, tabs[i], true, selectedTab);
                if (clicked)
                { customizationTab = i; customizationScroll = Vector2.zero; }
            }
            DrawCustomizationGrid();
            if (Button(new Rect(964, 773, 241, 44), "Back to game", true, true)) showSettings = false;
            GUI.matrix = oldMatrix;
        }
        void DrawCustomizationGrid()
        {
            if (customizationTab == 4)
            {
                DrawHoleCardOrderSetting();
                return;
            }
            Texture2D[] options = customizationTab == 1 ? cardBacks : customizationTab == 2 ? pokerTables : backgrounds;
            int count = customizationTab == 0 ? 3 : options.Length;
            const float tileWidth = 250, tileHeight = 190, gapX = 14, gapY = 14;
            int rows = Mathf.Max(1, Mathf.CeilToInt(count / 3f));
            float contentHeight = rows * (tileHeight + gapY) + 10;
            customizationScroll = GUI.BeginScrollView(new Rect(394, 318, 812, 430), customizationScroll,
                new Rect(0, 0, 812, contentHeight));
            if (count == 0) Label(20, 24, 760, 40, "No previews are available in this category.", small);
            for (int i = 0; i < count; i++)
            {
                int column = i % 3, row = i / 3;
                Rect tile = new Rect(10 + column * (tileWidth + gapX), 10 + row * (tileHeight + gapY), tileWidth, tileHeight);
                bool isSelected = customizationTab == 0 ? cardFaceStyle == i
                    : customizationTab == 1 ? cardBackIndex == i
                    : customizationTab == 2 ? tableIndex == i : backgroundIndex == i;
                Fill(tile, isSelected ? Accent : new Color(0.075f, 0.105f, 0.12f), rounded);
                Rect preview = new Rect(tile.x + 5, tile.y + 5, tile.width - 10, 148);
                Fill(preview, new Color(0.025f, 0.035f, 0.045f), rounded);
                string label;
                if (customizationTab == 0)
                {
                    string[] faceNames = { "Classic", "Four-color suits", "Four-color faces" };
                    const float cardWidth = 66, cardHeight = 92, cardStep = 44;
                    float firstCardX = tile.center.x - (cardWidth + cardStep * 3) / 2;
                    for (int suit = 0; suit < 4; suit++)
                        DrawCardFace(new Rect(firstCardX + suit * cardStep, tile.y + 28, cardWidth, cardHeight),
                            new CardView { rank = 14, suit = suit }, false, i);
                    label = faceNames[i];
                }
                else
                {
                    Texture2D previewImage = options[i];
                    ScaleMode scaleMode = customizationTab == 3 ? ScaleMode.ScaleAndCrop : ScaleMode.ScaleToFit;
                    GUI.DrawTexture(preview, previewImage, scaleMode, true);
                    label = previewImage.name;
                }
                Label(tile.x + 6, tile.y + 157, tile.width - 12, 26, label,
                    Style(16, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter));
                if (GUI.Button(tile, GUIContent.none, GUIStyle.none))
                {
                    if (customizationTab == 0)
                    { cardFaceStyle = i; PlayerPrefs.SetInt("drawmaha.card.face.style", i); }
                    else if (customizationTab == 1)
                    { cardBackIndex = i; cardBack = options[i]; PlayerPrefs.SetString("drawmaha.cardback.asset", cardBack.name); }
                    else if (customizationTab == 2)
                    { tableIndex = i; PlayerPrefs.SetString("drawmaha.pokertable.asset", options[i].name); }
                    else
                    { backgroundIndex = i; PlayerPrefs.SetString("drawmaha.background.asset", options[i].name); }
                    PlayerPrefs.Save();
                }
            }
            GUI.EndScrollView();
        }
        void DrawHoleCardOrderSetting()
        {
            customizationScroll = GUI.BeginScrollView(new Rect(394, 318, 812, 430), customizationScroll,
                new Rect(0, 0, 812, 430));
            Label(20, 24, 772, 38, "HOLE CARD ORDER", Style(20, Ink, FontStyle.Bold));
            string[] options = { "As dealt", "By rank", "By suit" };
            string[] descriptions = { "Keep the dealt order", "Highest rank first; suit breaks ties", "Clubs to spades; highest rank first" };
            for (int i = 0; i < options.Length; i++)
            {
                float x = 24 + i * 256;
                if (Button(new Rect(x, 90, 232, 54), options[i], true, holeCardOrderMode == i))
                {
                    holeCardOrderMode = i;
                    PlayerPrefs.SetInt("drawmaha.hole.card.order", i);
                    PlayerPrefs.Save();
                }
                Label(x, 153, 232, 48, descriptions[i], Style(14, Dim, FontStyle.Normal, TextAnchor.UpperCenter));
            }
            GUI.EndScrollView();
        }
    }
}
