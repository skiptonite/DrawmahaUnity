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
        Texture2D[] backgrounds, pokerTables;
        int backgroundIndex, tableIndex, avatarIndex;
        const float CardDealSeconds = 0.14f, BoardDealSeconds = 0.38f;
        readonly Color Accent = new Color(0.12f, 0.72f, 0.74f);
        readonly Color CardBackColor = new Color(0.075f, 0.24f, 0.38f);
        readonly Color CardFaceColor = new Color(0.98f, 0.97f, 0.92f);
        Texture2D white, round, rounded, felt, pokerChip, cardBack, buttonNormal, buttonHover, buttonPressed, accentNormal, accentHover;
        readonly Texture2D[] cardSuits = new Texture2D[4];
        readonly Texture2D[] cardSuitSources = new Texture2D[4];
        Font cardFont;
        bool ownsPokerChip;
        readonly Dictionary<string, Texture2D> profiles = new Dictionary<string, Texture2D>();
        readonly bool[] selected = new bool[5];
        readonly Vector2[] positions = { new Vector2(960, 720), new Vector2(435, 565), new Vector2(435, 345), new Vector2(960, 230), new Vector2(1485, 345), new Vector2(1485, 565) };
        readonly Vector2[] betPositions = { new Vector2(960, 628), new Vector2(610, 604), new Vector2(610, 410), new Vector2(960, 327), new Vector2(1310, 410), new Vector2(1310, 604) };
        GUIStyle text, small, bold, title, button, accentButton, input;
        string endpoint = "ws://localhost:8080/ws", username = "", invite = "", error = "", lastTable = "";
        bool post, showHistory, showRules, showSettings;
        Vector2 historyScroll;
        int lastHand = -1, shownBoard, lastActor = -99;
        float handStarted, nextBoardAt, raiseTo, errorUntil;
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
            white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply();
            round = CircleTexture();
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
            endpoint = PlayerPrefs.GetString("drawmaha.endpoint", endpoint); username = PlayerPrefs.GetString("drawmaha.username", "");
            network = gameObject.AddComponent<MultiplayerClient>();
            network.StateChanged += Received;
            network.Failure += s => { error = s; errorUntil = Time.unscaledTime + 8; };
#if UNITY_EDITOR
            if (UnityEditor.EditorPrefs.GetBool("Drawmaha.VisualPreview", false))
            {
                UnityEditor.EditorPrefs.DeleteKey("Drawmaha.VisualPreview");
                previewTable = CreateVisualPreview();
                shownBoard = previewTable.board.Length;
                handStarted = Time.unscaledTime - InitialDuration(previewTable);
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
            Destroy(buttonNormal); Destroy(buttonHover); Destroy(buttonPressed); Destroy(accentNormal); Destroy(accentHover);
            foreach (Texture2D suit in cardSuits) if (suit != null) Destroy(suit);
            foreach (var t in profiles.Values) Destroy(t);
        }
        void LoadCardStyleAssets()
        {
            cardBack = Resources.Load<Texture2D>("CardStyles/Backs/drawmaha_blue_card_back_720x1008");
            cardFont = Resources.Load<Font>("Fonts/AlienLeagueIiBold-wzWP");
            string[] suitNames = { "Club", "Diamond", "Heart", "Spade" };
            for (int suit = 0; suit < suitNames.Length; suit++)
            {
                Texture2D source = Resources.Load<Texture2D>("CardStyles/Suits/" + suitNames[suit]);
                if (source == null) continue;
                cardSuitSources[suit] = source;
                if (!source.isReadable) continue;
                Color32 tint = SuitColor(suit);
                const int outputSize = 384;
                var tintedPixels = new Color32[outputSize * outputSize];
                for (int y = 0; y < outputSize; y++) for (int x = 0; x < outputSize; x++)
                {
                    float u = (x + 0.5f) / outputSize, v = (y + 0.5f) / outputSize;
                    byte alpha = (byte)Mathf.RoundToInt(source.GetPixelBilinear(u, v).a * 255);
                    tintedPixels[y * outputSize + x] = new Color32(tint.r, tint.g, tint.b, alpha);
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
            if (state.id != lastTable || state.hand != lastHand)
            {
                lastTable = state.id; lastHand = state.hand; handStarted = Time.unscaledTime;
                shownBoard = 0; nextBoardAt = handStarted + InitialDuration(state) + 0.1f;
                Array.Clear(selected, 0, 5); previous = null;
            }
            if (state.actor == state.yourSeat && (lastActor != state.actor || previous == null || previous.stage != state.stage))
                raiseTo = Math.Min(state.minRaise, state.maxRaise);
            lastActor = state.actor; previous = state;
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
                id = "visual-preview", name = "Visual Preview", stage = "Draw", yourSeat = 0,
                hand = 42, dealer = 2, smallBlindSeat = 3, bigBlindSeat = 4, smallBlind = 5,
                bigBlind = 10, pot = 185, actor = 0, call = 20, minRaise = 30, maxRaise = 400,
                revision = 1, serverUnixMs = now, deadlineUnixMs = now + 45000, showdown = false,
                players = new SeatView[6], board = new[]
                {
                    new CardView { rank = 6, suit = 0 }, new CardView { rank = 7, suit = 1 },
                    new CardView { rank = 8, suit = 2 }, new CardView { rank = 9, suit = 3 },
                    new CardView { rank = 10, suit = 0 }
                },
                results = new[] { "Visual preview hand" },
                history = new[] { "Hand 42 · Button at seat 3.", "Northstar calls 20.", "Visual preview state." }
            };
            string[] names = { "You", "Northstar", "M. Alvarez", "Dealer", "Kestrel", "RiverRun" };
            for (int i = 0; i < state.players.Length; i++)
            {
                state.players[i] = new SeatView
                {
                    seat = i, avatar = i, stack = 850 + i * 175, bet = i == 1 ? 25 : i == 4 ? 50 : 0,
                    count = 5, username = names[i], status = "In hand", occupied = true,
                    connected = true, inHand = true,
                    cards = Enumerable.Range(i * 5, 5).Select(PreviewCard).ToArray()
                };
            }
            return state;
        }
        static CardView PreviewCard(int index) { return new CardView { rank = 2 + index % 13, suit = index / 13 % 4 }; }
        Texture2D CircleTexture()
        {
            const int size = 512; float center = size / 2f;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true); var pixels = new Color[size * size];
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(center, center));
                pixels[y * size + x] = new Color(1, 1, 1, Mathf.Clamp01(center + 0.5f - d));
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
            input = new GUIStyle(GUI.skin.textField); input.fontSize = 19; input.padding = new RectOffset(12, 12, 10, 8);
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
            bool wasEnabled = GUI.enabled; GUI.enabled = !showRules && !showHistory && !showSettings;
            Fill(new Rect(0, 0, W, 105), new Color(0.018f, 0.035f, 0.055f, 0.88f));
            Label(42, 19, 400, 46, "DRAWMAHA", title);
            Label(45, 66, 790, 24, currentTable == null ? "MULTIPLAYER  /  FIND YOUR TABLE" : currentTable.name.ToUpperInvariant() + "  /  " + currentTable.smallBlind + "–" + currentTable.bigBlind + "  /  POT LIMIT", small);
            if (Button(new Rect(1204, 27, 154, 48), "Settings")) showSettings = true;
            if (Button(new Rect(1370, 27, 112, 48), "Rules")) showRules = true;
            if (Button(new Rect(1494, 27, 132, 48), "History", currentTable != null)) showHistory = true;
            bool canLeave = currentTable == null || currentTable.stage == "Complete";
            if (currentTable != null && currentTable.yourSeat >= 0)
            {
                var own = currentTable.players[currentTable.yourSeat];
                canLeave |= !own.inHand || own.folded;
            }
            string leaveLabel = currentTable == null ? "Disconnect" : canLeave ? "Leave after hand" : "Fold before leaving";
            if (Button(new Rect(1638, 27, 240, 48), leaveLabel, network.Connected && !network.CommandPending && canLeave))
            {
                if (currentTable != null) network.Send(new Command { type = "leave" }); else network.Disconnect();
            }
            Fill(new Rect(40, 101, 1840, 1), new Color(0.35f, 0.49f, 0.56f, 0.55f));
            if (currentTable == null) Lobby(); else Table();
            Label(42, 1038, 1470, 25, Time.unscaledTime < errorUntil ? error : network.Status + (previewTable != null ? "  ·  Visual preview" : "  ·  Play chips"), small);
            GUI.enabled = wasEnabled;
            if (showRules) Rules(); if (showHistory) History(); if (showSettings) Settings();
            GUI.matrix = old;
        }
        void Lobby()
        {
            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.matrix = oldMatrix * Matrix4x4.Translate(new Vector3(160, 40, 0));
            Fill(new Rect(250, 159, 1100, 751), Panel); Label(286, 187, 600, 45, "TAKE A SEAT", title);
            Label(286, 239, 960, 38, "Six seats per table. Hands begin automatically with two players.", small);
            Label(286, 294, 180, 26, "Server address", small); endpoint = GUI.TextField(new Rect(286, 326, 680, 47), endpoint, input);
            Label(987, 294, 285, 26, "Username", small); username = GUI.TextField(new Rect(987, 326, 325, 47), username, 20, input);
            Label(286, 386, 260, 26, "Invite key", small); invite = GUI.PasswordField(new Rect(286, 417, 520, 47), invite, '*', 128, input);
            if (Button(new Rect(826, 417, 204, 47), "Avatar " + (avatarIndex + 1))) avatarIndex = (avatarIndex + 1) % 8;
            if (Button(new Rect(1050, 417, 262, 47), network.Connected ? "Connected" : "Connect", !network.Connected, true))
            {
                PlayerPrefs.SetString("drawmaha.endpoint", endpoint); PlayerPrefs.SetString("drawmaha.username", username); PlayerPrefs.Save();
                network.Connect(endpoint, username, avatarIndex, invite);
            }
            if (Button(new Rect(1050, 478, 262, 33), "Forget saved identity", !network.Connected)) network.ForgetSession(endpoint, username);
            Label(286, 497, 720, 28, "Entry preference:", small);
            if (Button(new Rect(470, 487, 227, 40), post ? "Post big blind ✓" : "Post big blind")) post = true;
            if (Button(new Rect(710, 487, 299, 40), post ? "Wait for big blind" : "Wait for big blind ✓")) post = false;
            Label(286, 561, 350, 26, "TABLE", small); Label(647, 561, 160, 26, "BLINDS", small);
            Label(832, 561, 180, 26, "BUY-IN", small); Label(1020, 561, 170, 26, "SEATS FILLED", small);
            var rows = network.Tables;
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i]; float y = 602 + i * 83;
                Fill(new Rect(276, y, 1044, 70), new Color(0.075f, 0.12f, 0.15f));
                Label(294, y + 20, 335, 35, row.name, bold); Label(649, y + 22, 160, 32, row.smallBlind + " / " + row.bigBlind);
                Label(834, y + 22, 170, 32, row.buyIn.ToString("N0")); Label(1020, y + 22, 145, 32, row.occupied + " / " + row.maxSeats);
                if (Button(new Rect(1180, y + 13, 121, 43), "Join", network.Connected && row.available > 0 && !network.CommandPending, true))
                    network.Send(new Command { type = "join", tableId = row.id, entryMode = post ? "post" : "wait" });
            }
            if (rows.Length == 0) Label(286, 632, 1000, 80, "Connect to your server to see available tables.\nFor a local test, run the server and open two game clients.", small);
            Label(286, 867, 1015, 30, "New arrivals join at a hand boundary. Posting is unavailable on the button or between the button and big blind.", Style(14, Dim));
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
            Label(720, 363, 480, 48, "POT  " + t.pot.ToString("N0"), Style(34, Ink, FontStyle.Bold, TextAnchor.MiddleCenter));
            for (int i = 0; i < shownBoard; i++)
            {
                float elapsed = Time.unscaledTime - (nextBoardAt - BoardDealSeconds);
                Vector2 target = new Vector2(715 + i * 100, 430);
                Vector2 pos = i == shownBoard - 1 ? Vector2.Lerp(new Vector2(915, 360), target, Mathf.SmoothStep(0, 1, Mathf.Clamp01(elapsed / 0.2f))) : target;
                Card(new Rect(pos.x, pos.y, 90, 126), t.board[i], false);
            }
            for (int i = 0; i < 6; i++) Seat(t, i);
            if (t.stage == "Complete" && shownBoard == t.board.Length)
                Label(650, 568, 620, 86, string.Join("\n", t.results.Take(3).ToArray()), Style(17, Ink, FontStyle.Normal, TextAnchor.MiddleCenter));
            else if (t.actor >= 0) Label(650, 568, 620, 32, t.actor == t.yourSeat ? (t.stage == "Draw" ? "YOUR DRAW · SELECT DISCARDS" : "YOUR TURN") : t.players[t.actor].username + "'s turn", Style(18, Ink, FontStyle.Bold, TextAnchor.MiddleCenter));
            Controls(t);
        }
        Texture2D Profile(SeatView player)
        {
            string key = player.avatar.ToString();
            Texture2D texture; if (profiles.TryGetValue(key, out texture)) return texture;
            const int size = 256; float center = size / 2f;
            texture = new Texture2D(size, size, TextureFormat.RGBA32, true); var pixels = new Color[size * size];
            Color baseColor = Color.HSVToRGB((player.avatar * 0.117f + 0.46f) % 1, 0.42f, 0.6f);
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                float distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(center, center));
                Color color = baseColor;
                color *= 0.75f + y / (size * 4f);
                color.a = Mathf.Clamp01(center + 0.5f - distance); pixels[y * size + x] = color;
            }
            texture.SetPixels(pixels); texture.Apply(); texture.filterMode = FilterMode.Trilinear; texture.wrapMode = TextureWrapMode.Clamp; profiles[key] = texture; return texture;
        }
        void Seat(TableState t, int actual)
        {
            int relative = (actual - Math.Max(0, t.yourSeat) + 6) % 6; Vector2 center = positions[relative]; var p = t.players[actual];
            Rect box = new Rect(center.x - 118, center.y, 236, 78);
            if (!p.occupied) return;
            bool own = actual == t.yourSeat, acting = actual == t.actor;
            DrawSeatPanel(t, p, actual, box, own, acting);
            if (!p.inHand || p.folded || t.players.Count(player => player.inHand && !player.folded) <= 1)
            {
                DrawDealerButton(t, actual, box);
                DrawBetChip(p, relative);
                return;
            }
            int[] order = Enumerable.Range(1, 6).Select(n => (t.dealer + n + 6) % 6).Where(i => t.players[i].inHand).ToArray();
            int ordinal = Array.IndexOf(order, actual); int steps = Mathf.FloorToInt((Time.unscaledTime - handStarted) / CardDealSeconds) + 1;
            int visible = Math.Min(p.count, Mathf.Clamp((steps - ordinal + order.Length - 1) / Math.Max(1, order.Length), 0, 5));
            bool previewReveal = previewTable != null && (actual == 2 || actual == 4);
            bool reveal = own || previewReveal || (t.showdown && shownBoard == t.board.Length);
            float width = own ? 72 : 60, height = own ? 102 : 84, gap = width * 0.40f;
            int layoutCount = Mathf.Clamp(p.count, 1, 5); float middle = (layoutCount - 1) * 0.5f;
            float cardTop = center.y - height + 12;
            Rect[] rects = new Rect[visible]; float[] angles = new float[visible];
            for (int i = 0; i < visible; i++)
            {
                float lift = own && selected[i] ? 18 : 0;
                Vector2 target = new Vector2(center.x - (gap * (layoutCount - 1) + width) / 2 + i * gap, cardTop - lift + Mathf.Abs(i - middle) * 1.25f);
                float age = Time.unscaledTime - handStarted - (i * order.Length + ordinal) * CardDealSeconds;
                Vector2 pos = Vector2.Lerp(new Vector2(915, 395), target, Mathf.SmoothStep(0, 1, Mathf.Clamp01(age / 0.2f)));
                rects[i] = new Rect(pos.x, pos.y, width, height); angles[i] = (i - middle) * 2f;
                Matrix4x4 saved = GUI.matrix; GUI.matrix = saved * RotationAtCanvasPivot(angles[i], new Vector2(pos.x + width / 2, pos.y + height));
                Card(rects[i], reveal && p.cards != null && i < p.cards.Length ? p.cards[i] : null, own && selected[i]); GUI.matrix = saved;
            }
            DrawSeatPanel(t, p, actual, box, own, acting);
            DrawDealerButton(t, actual, box);
            DrawBetChip(p, relative);
            if (own && t.actor == actual && t.stage == "Draw" && (previewTable != null || network.Connected) && !network.CommandPending && GUI.enabled && Time.unscaledTime >= handStarted + InitialDuration(t))
            {
                // Reverse hit order matches the visual stacking of overlapping cards.
                for (int i = visible - 1; i >= 0; i--)
                {
                    Matrix4x4 saved = GUI.matrix; GUI.matrix = saved * RotationAtCanvasPivot(angles[i], new Vector2(rects[i].x + width / 2, rects[i].y + height));
                    Color old = GUI.color; GUI.color = Color.clear;
                    if (GUI.Button(rects[i], GUIContent.none))
                    {
                        if (selected[i]) selected[i] = false;
                        else if (selected.Count(s => s) < 2) selected[i] = true;
                    }
                    GUI.color = old; GUI.matrix = saved;
                }
            }
        }
        void DrawSeatPanel(TableState t, SeatView p, int actual, Rect box, bool own, bool acting)
        {
            if (acting)
                Fill(new Rect(box.x - 3, box.y - 3, box.width + 6, box.height + 6), Color.Lerp(Accent, Color.white, 0.14f + 0.10f * Mathf.Sin(Time.unscaledTime * 4)), rounded);
            Fill(box, new Color(0.025f, 0.075f, 0.13f, 0.98f), rounded);
            Fill(new Rect(box.x + 9, box.y + 11, 56, 56), Color.white, Profile(p));
            Label(box.x + 9, box.y + 18, 56, 42, p.username.Length > 0 ? p.username.Substring(0, 1).ToUpperInvariant() : "?", Style(25, Ink, FontStyle.Bold, TextAnchor.MiddleCenter));
            string status = p.status.ToUpperInvariant();
            if (acting)
            {
                double now = t.serverUnixMs + (Time.unscaledTime - network.StateReceivedAt) * 1000.0;
                status = (own ? "YOUR TURN" : "ACTING") + "  ·  " + Math.Max(0, Math.Ceiling((t.deadlineUnixMs - now) / 1000)) + "s";
            }
            bool resultWinner = t.stage == "Complete" && p.winner;
            Label(box.x + 75, box.y + 7, 151, 22, own ? p.username + " (you)" : p.username, Style(15, Ink, FontStyle.Bold));
            Label(box.x + 75, box.y + 29, 151, 20, p.stack.ToString("N0") + " chips", Style(13, Accent));
            Label(box.x + 75, box.y + 50, 151, 18, resultWinner ? (p.scoop ? "SCOOP!" : "Winner") : status,
                Style(11, resultWinner || acting ? Accent : Dim, FontStyle.Bold));
        }
        void DrawDealerButton(TableState t, int actual, Rect box)
        {
            if (t.hand <= 0 || actual != t.dealer) return;
            Fill(new Rect(box.x + box.width - 20, box.y - 12, 26, 26), Accent, round);
            Label(box.x + box.width - 20, box.y - 11, 26, 23, "D", Style(13, Panel, FontStyle.Bold, TextAnchor.MiddleCenter));
        }
        void DrawBetChip(SeatView player, int relative)
        {
            if (player.bet <= 0) return;
            Vector2 center = betPositions[relative];
            Rect chip = new Rect(center.x - 23, center.y - 23, 46, 46);
            Fill(chip, Color.white, pokerChip);
            Label(chip.x - 15, chip.yMax + 1, chip.width + 30, 21, player.bet.ToString("N0"), Style(15, new Color(0.035f, 0.07f, 0.045f), FontStyle.Bold, TextAnchor.MiddleCenter));
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
            Fill(new Rect(rect.x - 1.5f * cardScale, rect.y - 1.5f * cardScale, rect.width + 3 * cardScale, rect.height + 3 * cardScale), marked ? Accent : new Color(0.82f, 0.84f, 0.83f), rounded);
            if (face == null)
            {
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
            var value = new Drawmaha.Card(face.rank, face.suit);
            Color suitColor = SuitColor(face.suit);
            Fill(rect, CardFaceColor, rounded);
            float cornerWidth = 36f * cardScale;
            int cornerRankSize = Mathf.RoundToInt(54f * cardScale);
            float cornerRankHeight = rect.height * 0.46f;
            float cornerIndexX = rect.x + 2 * cardScale;
            float cornerIndexCenter = cornerIndexX + cornerWidth / 2;
            Label(cornerIndexX, rect.y + cardScale, cornerWidth, cornerRankHeight, value.RankText,
                Style(cornerRankSize, suitColor, FontStyle.Normal, TextAnchor.MiddleCenter, cardFont));
            float cornerSuitSize = 31.5f * cardScale;
            DrawSuit(new Rect(cornerIndexCenter - cornerSuitSize / 2, rect.y + rect.height * 0.40f,
                cornerSuitSize, cornerSuitSize),
                face.suit, value.SuitText, suitColor);
            float centerSuitSize = 70f * cardScale;
            Rect largeSuit = new Rect(rect.xMax - centerSuitSize + 6 * cardScale,
                rect.yMax - centerSuitSize + 5 * cardScale, centerSuitSize, centerSuitSize);
            DrawSuit(largeSuit, face.suit, value.SuitText, suitColor);
            if (marked)
            {
                float bannerHeight = 19 * cardScale;
                Fill(new Rect(rect.x, rect.y + rect.height - bannerHeight, rect.width, bannerHeight), Accent, rounded);
                Label(rect.x, rect.y + rect.height - bannerHeight, rect.width, bannerHeight, "DISCARD",
                    Style(Mathf.RoundToInt(11 * cardScale), Panel, FontStyle.Bold, TextAnchor.MiddleCenter));
            }
        }
        static Color SuitColor(int suit)
        {
            switch (suit)
            {
                case 1:
                case 2: return new Color(0.76f, 0.08f, 0.13f);
                default: return new Color(0.08f, 0.09f, 0.11f);
            }
        }
        void DrawSuit(Rect rect, int suit, string fallback, Color color)
        {
            if (suit >= 0 && suit < cardSuits.Length && cardSuits[suit] != null)
                Fill(rect, Color.white, cardSuits[suit]);
            else if (suit >= 0 && suit < cardSuitSources.Length && cardSuitSources[suit] != null)
                Fill(rect, Color.white, cardSuitSources[suit]);
            else Label(rect.x, rect.y, rect.width, rect.height, fallback, Style(Mathf.RoundToInt(rect.height), color, FontStyle.Bold, TextAnchor.MiddleCenter));
        }
        void Controls(TableState t)
        {
            Fill(new Rect(1230, 880, 650, 184), Panel, rounded);
            var own = t.players[t.yourSeat];
            bool animationsDone = Time.unscaledTime >= handStarted + InitialDuration(t) && shownBoard == t.board.Length;
            bool enabled = network.Connected && !network.CommandPending && !own.leaving && animationsDone;
            if (!own.inHand || t.stage == "Waiting")
            {
                Label(1254, 894, 580, 29, own.status, small);
                if (Button(new Rect(1250, 966, 275, 52), "Post big blind", enabled)) network.Send(new Command { type = "entry", entryMode = "post" });
                if (Button(new Rect(1537, 966, 318, 52), "Wait for big blind", enabled)) network.Send(new Command { type = "entry", entryMode = "wait" }); return;
            }
            if (t.stage == "Complete") { Label(1254, 925, 580, 40, "Next hand starts automatically when two players are available.", small); return; }
            if (t.actor != t.yourSeat || own.folded || own.leaving) { Label(1254, 925, 580, 44, own.leaving ? "You will leave after this hand. Your remaining turns fold automatically." : animationsDone ? "Waiting for the other players…" : "Dealing cards…", small); return; }
            if (t.stage == "Draw")
            {
                int count = selected.Count(s => s); Label(1254, 901, 570, 45, "Select up to two cards above.  " + count + "/2 selected.", small);
                if (Button(new Rect(1250, 966, 605, 52), count == 0 ? "Stand pat" : "Draw " + count + " cards", enabled, true))
                { network.Send(new Command { type = "draw", discards = Enumerable.Range(0, 5).Where(i => selected[i]).ToArray() }); Array.Clear(selected, 0, 5); } return;
            }
            int min = Math.Min(t.minRaise, t.maxRaise), max = t.maxRaise;
            if (t.canRaise)
            {
                raiseTo = Mathf.Clamp(raiseTo, min, max);
                Label(1254, 887, 580, 27, "RAISE TO  " + Mathf.RoundToInt(raiseTo).ToString("N0"), Style(15, Dim, FontStyle.Bold));
                if (Button(new Rect(1250, 918, 190, 36), "Minimum", enabled)) raiseTo = min;
                if (Button(new Rect(1450, 918, 190, 36), "Middle", enabled)) raiseTo = Mathf.RoundToInt(Mathf.Lerp(min, max, 0.5f));
                if (Button(new Rect(1650, 918, 205, 36), "Pot", enabled)) raiseTo = max;
                raiseTo = Mathf.Clamp(GUI.HorizontalSlider(new Rect(1264, 959, 578, 20), raiseTo, min, Math.Max(min + 1, max)), min, max);
            }
            if (Button(new Rect(1250, 990, 164, 54), "Fold", enabled)) network.Send(new Command { type = "fold" });
            if (Button(new Rect(1424, 990, 194, 54), t.call == 0 ? "Check" : "Call " + t.call.ToString("N0"), enabled, true)) network.Send(new Command { type = "call" });
            if (Button(new Rect(1628, 990, 227, 54), "Raise", enabled && t.canRaise, true)) network.Send(new Command { type = "raise", raiseTo = Mathf.RoundToInt(raiseTo) });
        }
        void Overlay(string heading)
        {
            Fill(new Rect(0, 0, W, H), new Color(0, 0, 0, 0.85f)); Fill(new Rect(350, 160, 900, 680), Panel); Label(394, 193, 805, 55, heading, title);
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
            GUI.matrix = oldMatrix * Matrix4x4.Translate(new Vector3(160, 40, 0));
            var state = CurrentTable;
            Overlay("TABLE HISTORY"); var lines = state != null ? state.history : new string[0];
            historyScroll = GUI.BeginScrollView(new Rect(394, 270, 811, 480), historyScroll, new Rect(0, 0, 777, Math.Max(460, lines.Length * 46)));
            for (int i = 0; i < lines.Length; i++) Label(0, i * 46, 764, 44, lines[i], small);
            GUI.EndScrollView(); if (Button(new Rect(964, 773, 241, 44), "Back to game", true, true)) showHistory = false;
            GUI.matrix = oldMatrix;
        }
        void ImageChoice(float y, string label, Texture2D[] options, ref int index, bool crop)
        {
            Fill(new Rect(380, y, 840, 190), new Color(0.075f, 0.105f, 0.12f), rounded);
            Label(405, y + 12, 350, 30, label.ToUpperInvariant(), bold);
            Fill(new Rect(405, y + 51, 270, 120), new Color(0.025f, 0.035f, 0.045f), rounded);
            if (options.Length == 0)
            {
                Label(690, y + 80, 480, 38, "Add images to the matching Resources folder.", small);
                return;
            }
            index = Mathf.Clamp(index, 0, options.Length - 1);
            GUI.DrawTexture(new Rect(410, y + 56, 260, 110), options[index], crop ? ScaleMode.ScaleAndCrop : ScaleMode.ScaleToFit, true);
            if (Button(new Rect(700, y + 72, 50, 50), "<", options.Length > 1)) index = (index - 1 + options.Length) % options.Length;
            Label(765, y + 78, 375, 40, options[index].name, bold);
            Label(765, y + 119, 375, 32, (index + 1) + " of " + options.Length, small);
            if (Button(new Rect(1150, y + 72, 50, 50), ">", options.Length > 1)) index = (index + 1) % options.Length;
        }
        void Settings()
        {
            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.matrix = oldMatrix * Matrix4x4.Translate(new Vector3(160, 40, 0));
            Overlay("SETTINGS");
            ImageChoice(278, "Background", backgrounds, ref backgroundIndex, true);
            ImageChoice(488, "Poker table", pokerTables, ref tableIndex, false);
            if (Button(new Rect(964, 773, 241, 44), "Save settings", true, true))
            {
                if (backgrounds.Length > 0) PlayerPrefs.SetString("drawmaha.background.asset", backgrounds[backgroundIndex].name);
                if (pokerTables.Length > 0) PlayerPrefs.SetString("drawmaha.pokertable.asset", pokerTables[tableIndex].name);
                PlayerPrefs.Save(); showSettings = false;
            }
            GUI.matrix = oldMatrix;
        }
    }
}
