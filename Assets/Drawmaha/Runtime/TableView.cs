using System;
using System.Collections.Generic;
using System.Linq;
using Drawmaha.Network;
using UnityEngine;

namespace Drawmaha
{
    public sealed class TableView : MonoBehaviour
    {
        const float W = 1600, H = 1000;
        MultiplayerClient network;
        TableState previewTable;
        TableState CurrentTable { get { return previewTable ?? network.Table; } }
        TableTheme[] themes;
        int backgroundIndex, tableIndex, cardsIndex, avatarIndex;
        Texture2D white, round, felt;
        readonly Dictionary<string, Texture2D> profiles = new Dictionary<string, Texture2D>();
        readonly bool[] selected = new bool[5];
        readonly Vector2[] positions = { new Vector2(800, 800), new Vector2(340, 655), new Vector2(340, 300), new Vector2(800, 235), new Vector2(1260, 300), new Vector2(1260, 655) };
        GUIStyle text, small, bold, title, button, input;
        string endpoint = "ws://localhost:8080/ws", username = "", invite = "", error = "", lastTable = "";
        bool post, showHistory, showRules, showSettings;
        Vector2 historyScroll;
        int lastHand = -1, shownBoard, lastActor = -99;
        float handStarted, nextBoardAt, raiseTo, errorUntil;
        TableState previous;
        Color Ink { get { return new Color(0.91f, 0.94f, 0.96f); } }
        Color Dim { get { return new Color(0.53f, 0.64f, 0.68f); } }
        Color Panel { get { return new Color(0.048f, 0.08f, 0.11f); } }
        TableTheme BackgroundTheme { get { return themes[backgroundIndex % themes.Length]; } }
        TableTheme Theme { get { return themes[tableIndex % themes.Length]; } }
        TableTheme Cards { get { return themes[cardsIndex % themes.Length]; } }

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
            themes = Resources.LoadAll<TableTheme>("TableThemes");
            if (themes.Length == 0)
            {
                themes = new TableTheme[3];
                for (int i = 0; i < 3; i++) themes[i] = ScriptableObject.CreateInstance<TableTheme>();
                themes[0].DisplayName = "Emerald";
                themes[1].DisplayName = "Midnight"; themes[1].Felt = new Color(0.075f, 0.16f, 0.28f); themes[1].Background = new Color(0.025f, 0.03f, 0.07f); themes[1].CardBack = new Color(0.15f, 0.14f, 0.31f);
                themes[2].DisplayName = "Burgundy"; themes[2].Felt = new Color(0.28f, 0.075f, 0.12f); themes[2].Background = new Color(0.07f, 0.025f, 0.035f); themes[2].CardBack = new Color(0.28f, 0.09f, 0.12f);
            }
            backgroundIndex = Math.Abs(PlayerPrefs.GetInt("drawmaha.background", 0)) % themes.Length;
            tableIndex = Math.Abs(PlayerPrefs.GetInt("drawmaha.table", 0)) % themes.Length;
            cardsIndex = Math.Abs(PlayerPrefs.GetInt("drawmaha.cards", 0)) % themes.Length;
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
        void OnDestroy()
        {
            Destroy(white); Destroy(round); Destroy(felt);
            foreach (var t in profiles.Values) Destroy(t);
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
        float InitialDuration(TableState state) { return state.players.Count(p => p.inHand) * 5 * Theme.CardDealSeconds; }
        void Update()
        {
            var state = CurrentTable;
            if (state == null) return;
            if (shownBoard < state.board.Length && Time.unscaledTime >= nextBoardAt && Time.unscaledTime >= handStarted + InitialDuration(state))
            {
                shownBoard++; nextBoardAt = Time.unscaledTime + Theme.BoardDealSeconds;
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
                revision = 1, serverUnixMs = now, deadlineUnixMs = now + 45000, showdown = true,
                players = new SeatView[6], board = Enumerable.Range(30, 5).Select(PreviewCard).ToArray(),
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
            var texture = new Texture2D(96, 96); var pixels = new Color[96 * 96];
            for (int y = 0; y < 96; y++) for (int x = 0; x < 96; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(48, 48));
                pixels[y * 96 + x] = new Color(1, 1, 1, Mathf.Clamp01(48 - d));
            }
            texture.SetPixels(pixels); texture.Apply(); return texture;
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
                    if (distance > 0.92f) color = Theme.Rail;
                    else if (distance > 0.909f) color = Theme.Accent * 0.7f;
                    else color = Theme.Felt * (1f - distance * 0.16f + ((x * 17 + y * 31) % 13) / 1100f);
                    color.a = 1;
                }
                pixels[y * width + x] = color;
            }
            felt.SetPixels(pixels); felt.Apply();
        }
        GUIStyle Style(int size, Color color, FontStyle weight = FontStyle.Normal, TextAnchor align = TextAnchor.UpperLeft)
        {
            var s = new GUIStyle(GUI.skin.label); s.fontSize = size; s.fontStyle = weight; s.normal.textColor = color;
            s.alignment = align; s.wordWrap = true; s.richText = false; s.padding = new RectOffset(0, 0, 0, 0); return s;
        }
        void Styles()
        {
            text = Style(19, Ink); small = Style(15, Dim); bold = Style(20, Ink, FontStyle.Bold); title = Style(34, Ink, FontStyle.Bold);
            button = new GUIStyle(GUI.skin.button); button.fontSize = 17; button.fontStyle = FontStyle.Bold; button.normal.textColor = Ink;
            button.hover.textColor = Ink; button.active.textColor = Ink; button.normal.background = white; button.hover.background = white; button.active.background = white;
            input = new GUIStyle(GUI.skin.textField); input.fontSize = 19; input.padding = new RectOffset(12, 12, 10, 8);
        }
        void Fill(Rect rect, Color color, Texture2D texture = null)
        {
            Color old = GUI.color; GUI.color = color; GUI.DrawTexture(rect, texture ?? white, ScaleMode.StretchToFill, true); GUI.color = old;
        }
        void Label(float x, float y, float width, float height, string value, GUIStyle style = null) { GUI.Label(new Rect(x, y, width, height), value, style ?? text); }
        bool Button(Rect rect, string value, bool enabled = true, bool accent = false)
        {
            bool wasEnabled = GUI.enabled; Color old = GUI.backgroundColor; GUI.enabled = wasEnabled && enabled;
            GUI.backgroundColor = accent ? new Color(0.12f, 0.38f, 0.32f) : new Color(0.12f, 0.19f, 0.24f);
            bool click = GUI.Button(rect, value, button); GUI.enabled = wasEnabled; GUI.backgroundColor = old; return click;
        }
        void OnGUI()
        {
            if (network == null) return; if (text == null) Styles();
            var currentTable = CurrentTable;
            float scale = Mathf.Min(Screen.width / W, Screen.height / H); Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(new Vector3((Screen.width - W * scale) / 2, (Screen.height - H * scale) / 2, 0), Quaternion.identity, new Vector3(scale, scale, 1));
            Fill(new Rect(0, 0, W, H), BackgroundTheme.Background);
            if (BackgroundTheme.BackgroundImage != null) Fill(new Rect(0, 0, W, H), Color.white, BackgroundTheme.BackgroundImage);
            bool wasEnabled = GUI.enabled; GUI.enabled = !showRules && !showHistory && !showSettings;
            Label(36, 23, 390, 45, "DRAWMAHA", title);
            Label(38, 73, 600, 22, currentTable == null ? "MULTIPLAYER  /  FIND YOUR TABLE" : currentTable.name.ToUpperInvariant() + "  /  " + currentTable.smallBlind + "–" + currentTable.bigBlind + "  /  POT LIMIT", small);
            if (Button(new Rect(944, 31, 140, 42), "Appearance")) showSettings = true;
            if (Button(new Rect(1096, 31, 102, 42), "Rules")) showRules = true;
            if (Button(new Rect(1210, 31, 118, 42), "History", currentTable != null)) showHistory = true;
            bool canLeave = currentTable == null || currentTable.stage == "Complete";
            if (currentTable != null && currentTable.yourSeat >= 0)
            {
                var own = currentTable.players[currentTable.yourSeat];
                canLeave |= !own.inHand || own.folded;
            }
            string leaveLabel = currentTable == null ? "Disconnect" : canLeave ? "Leave after hand" : "Fold before leaving";
            if (Button(new Rect(1340, 31, 220, 42), leaveLabel, network.Connected && !network.CommandPending && canLeave))
            {
                if (currentTable != null) network.Send(new Command { type = "leave" }); else network.Disconnect();
            }
            Fill(new Rect(36, 110, 1528, 1), new Color(0.15f, 0.23f, 0.27f));
            if (currentTable == null) Lobby(); else Table();
            Label(38, 968, 1450, 26, Time.unscaledTime < errorUntil ? error : network.Status + (previewTable != null ? "  ·  Visual preview" : "  ·  Play chips"), small);
            GUI.enabled = wasEnabled;
            if (showRules) Rules(); if (showHistory) History(); if (showSettings) Settings();
            GUI.matrix = old;
        }
        void Lobby()
        {
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
        }
        void Table()
        {
            var t = CurrentTable;
            Fill(new Rect(209, 254, 1182, 568), Color.white, Theme.TableImage != null ? Theme.TableImage : felt);
            Label(582, 341, 436, 27, t.hand == 0 ? "WAITING FOR PLAYERS" : "HAND " + t.hand + "  /  " + t.stage.ToUpperInvariant(), Style(16, Theme.Accent, FontStyle.Bold, TextAnchor.MiddleCenter));
            Label(590, 382, 420, 46, "POT  " + t.pot.ToString("N0"), Style(32, Ink, FontStyle.Bold, TextAnchor.MiddleCenter));
            for (int i = 0; i < shownBoard; i++)
            {
                float elapsed = Time.unscaledTime - (nextBoardAt - Theme.BoardDealSeconds);
                Vector2 target = new Vector2(585 + i * 89, 449);
                Vector2 pos = i == shownBoard - 1 ? Vector2.Lerp(new Vector2(785, 353), target, Mathf.SmoothStep(0, 1, Mathf.Clamp01(elapsed / 0.2f))) : target;
                Card(new Rect(pos.x, pos.y, 75, 106), t.board[i], false, true);
            }
            for (int i = 0; i < 6; i++) Seat(t, i);
            if (t.stage == "Complete" && shownBoard == t.board.Length)
                Label(500, 581, 600, 91, string.Join("\n", t.results.Take(3).ToArray()), Style(16, Ink, FontStyle.Normal, TextAnchor.MiddleCenter));
            else if (t.actor >= 0) Label(545, 582, 510, 42, t.actor == t.yourSeat ? (t.stage == "Draw" ? "YOUR DRAW · SELECT DISCARDS" : "YOUR TURN") : t.players[t.actor].username + "'s turn", Style(17, Ink, FontStyle.Bold, TextAnchor.MiddleCenter));
            Controls(t);
        }
        Texture2D Profile(SeatView player)
        {
            string key = tableIndex + ":" + player.avatar;
            Texture2D texture; if (profiles.TryGetValue(key, out texture)) return texture;
            Texture2D source = Theme.Avatars != null && player.avatar < Theme.Avatars.Length ? Theme.Avatars[player.avatar] : null;
            texture = new Texture2D(96, 96); var pixels = new Color[96 * 96];
            Color baseColor = Color.HSVToRGB((player.avatar * 0.117f + 0.46f) % 1, 0.42f, 0.6f);
            for (int y = 0; y < 96; y++) for (int x = 0; x < 96; x++)
            {
                float distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(48, 48));
                Color color = baseColor;
                if (source != null && source.isReadable) color = source.GetPixelBilinear(x / 95f, y / 95f);
                else color *= 0.75f + y / 384f;
                color.a = Mathf.Clamp01(48 - distance); pixels[y * 96 + x] = color;
            }
            texture.SetPixels(pixels); texture.Apply(); profiles[key] = texture; return texture;
        }
        void Seat(TableState t, int actual)
        {
            int relative = (actual - Math.Max(0, t.yourSeat) + 6) % 6; Vector2 center = positions[relative]; var p = t.players[actual];
            Rect box = new Rect(center.x - 129, center.y, 258, 82);
            if (!p.occupied) return;
            bool own = actual == t.yourSeat, acting = actual == t.actor;
            if (acting) Fill(new Rect(box.x - 4, box.y - 4, box.width + 8, box.height + 8), Color.Lerp(Theme.Accent, Color.white, 0.14f + 0.10f * Mathf.Sin(Time.unscaledTime * 4)));
            Fill(box, Panel); Fill(new Rect(box.x + 10, box.y + 9, 64, 64), Color.white, Profile(p));
            bool customAvatar = Theme.Avatars != null && p.avatar < Theme.Avatars.Length && Theme.Avatars[p.avatar] != null && Theme.Avatars[p.avatar].isReadable;
            if (!customAvatar) Label(box.x + 10, box.y + 21, 64, 44, p.username.Length > 0 ? p.username.Substring(0, 1).ToUpperInvariant() : "?", Style(30, Ink, FontStyle.Bold, TextAnchor.MiddleCenter));
            bool resultWinner = t.stage == "Complete" && p.winner;
            if (resultWinner)
            {
                Label(box.x + 87, box.y + 8, 163, 26, own ? p.username + " (you)" : p.username, Style(18, Ink, FontStyle.Bold));
                Label(box.x + 87, box.y + 34, 163, 20, p.stack.ToString("N0") + " chips", Style(16, Theme.Accent));
                Label(box.x + 87, box.y + 56, 163, 20, p.scoop ? "SCOOP!" : "Winner", Style(17, Theme.Accent, FontStyle.Bold));
            }
            else
            {
                Label(box.x + 87, box.y + 12, 163, 30, own ? p.username + " (you)" : p.username, Style(18, Ink, FontStyle.Bold));
                Label(box.x + 87, box.y + 43, 163, 29, p.stack.ToString("N0") + " chips", Style(17, Theme.Accent));
            }
            string status = p.status;
            if (acting)
            {
                double now = t.serverUnixMs + (Time.unscaledTime - network.StateReceivedAt) * 1000.0;
                status = "YOUR TURN" + "  ·  " + Math.Max(0, Math.Ceiling((t.deadlineUnixMs - now) / 1000)) + "s";
                if (!own) status = "Acting  ·  " + Math.Max(0, Math.Ceiling((t.deadlineUnixMs - now) / 1000)) + "s";
            }
            Label(box.x - 20, box.y + 91, box.width + 40, 44, status, Style(14, acting ? Theme.Accent : Dim, FontStyle.Normal, TextAnchor.MiddleCenter));
            if (t.hand > 0 && actual == t.dealer)
            {
                Fill(new Rect(box.x + 230, box.y - 33, 30, 30), Theme.Accent, round);
                Label(box.x + 230, box.y - 31, 30, 25, "D", Style(14, Panel, FontStyle.Bold, TextAnchor.MiddleCenter));
            }
            if (p.bet > 0) Label(box.x + 15, box.y - (own ? 169 : 136), 228, 34, "●  " + p.bet.ToString("N0"), Style(22, Theme.Accent, FontStyle.Bold, TextAnchor.MiddleCenter));
            if (!p.inHand || p.folded || t.players.Count(player => player.inHand && !player.folded) <= 1) return;
            int[] order = Enumerable.Range(1, 6).Select(n => (t.dealer + n + 6) % 6).Where(i => t.players[i].inHand).ToArray();
            int ordinal = Array.IndexOf(order, actual); int steps = Mathf.FloorToInt((Time.unscaledTime - handStarted) / Theme.CardDealSeconds) + 1;
            int visible = Math.Min(p.count, Mathf.Clamp((steps - ordinal + order.Length - 1) / Math.Max(1, order.Length), 0, 5));
            bool reveal = own || (t.showdown && shownBoard == t.board.Length);
            float width = own ? 68 : 58, height = own ? 96 : 82, gap = own ? 31 : 27;
            int layoutCount = Mathf.Clamp(p.count, 1, 5); float middle = (layoutCount - 1) * 0.5f;
            Rect[] rects = new Rect[visible]; float[] angles = new float[visible];
            for (int i = 0; i < visible; i++)
            {
                float lift = own && selected[i] ? 18 : 0;
                Vector2 target = new Vector2(center.x - (gap * (layoutCount - 1) + width) / 2 + i * gap, center.y - height - 12 - lift + Mathf.Abs(i - middle) * 2.5f);
                float age = Time.unscaledTime - handStarted - (i * order.Length + ordinal) * Theme.CardDealSeconds;
                Vector2 pos = Vector2.Lerp(new Vector2(775, 353), target, Mathf.SmoothStep(0, 1, Mathf.Clamp01(age / 0.2f)));
                rects[i] = new Rect(pos.x, pos.y, width, height); angles[i] = (i - middle) * 3.5f;
                Matrix4x4 saved = GUI.matrix; GUI.matrix = saved * RotationAtCanvasPivot(angles[i], new Vector2(pos.x + width / 2, pos.y + height));
                Card(rects[i], reveal && p.cards != null && i < p.cards.Length ? p.cards[i] : null, own && selected[i], i == visible - 1); GUI.matrix = saved;
            }
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
        static Matrix4x4 RotationAtCanvasPivot(float angle, Vector2 pivot)
        {
            Vector3 point = new Vector3(pivot.x, pivot.y, 0);
            return Matrix4x4.Translate(point) * Matrix4x4.Rotate(Quaternion.Euler(0, 0, angle)) * Matrix4x4.Translate(-point);
        }
        void Card(Rect rect, CardView face, bool marked, bool showCenterRank)
        {
            Fill(new Rect(rect.x + 2, rect.y + 4, rect.width, rect.height), new Color(0, 0, 0, 0.25f));
            Fill(new Rect(rect.x - 1.5f, rect.y - 1.5f, rect.width + 3, rect.height + 3), marked ? Theme.Accent : new Color(0.47f, 0.51f, 0.52f));
            if (face == null)
            {
                Fill(rect, Cards.CardBack);
                if (Cards.CardBackImage != null) Fill(rect, Color.white, Cards.CardBackImage);
                else
                {
                    Fill(new Rect(rect.x + 5, rect.y + 5, rect.width - 10, rect.height - 10), Cards.CardBack * 1.35f);
                    Label(rect.x, rect.y + rect.height / 2 - 15, rect.width, 32, "D", Style(20, Theme.Accent, FontStyle.Bold, TextAnchor.MiddleCenter));
                }
                return;
            }
            var value = new Drawmaha.Card(face.rank, face.suit);
            Color suitColor = SuitColor(face.suit);
            Fill(rect, suitColor);
            if (Cards.CardFaceImage != null) Fill(rect, suitColor, Cards.CardFaceImage);
            float cornerWidth = Mathf.Min(rect.width - 8, 34);
            Label(rect.x + 4, rect.y + 3, cornerWidth, 25, value.RankText, Style(rect.width > 60 ? 21 : 18, Color.white, FontStyle.Bold, TextAnchor.UpperLeft));
            Label(rect.x + 4, rect.y + 24, cornerWidth, 24, value.SuitText, Style(rect.width > 60 ? 20 : 17, Color.white, FontStyle.Normal, TextAnchor.UpperLeft));
            if (showCenterRank)
                Label(rect.x + 3, rect.y + 3, rect.width - 6, rect.height - 6, value.RankText,
                    Style(rect.width >= 75 ? 46 : rect.width > 60 ? 36 : 30, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter));
            if (marked) { Fill(new Rect(rect.x, rect.y + rect.height - 17, rect.width, 17), Theme.Accent); Label(rect.x, rect.y + rect.height - 17, rect.width, 17, "DISCARD", Style(10, Panel, FontStyle.Bold, TextAnchor.MiddleCenter)); }
        }
        static Color SuitColor(int suit)
        {
            switch (suit)
            {
                case 0: return new Color(0.08f, 0.43f, 0.19f);
                case 1: return new Color(0.12f, 0.34f, 0.72f);
                case 2: return new Color(0.74f, 0.10f, 0.16f);
                default: return new Color(0.08f, 0.09f, 0.11f);
            }
        }
        void Controls(TableState t)
        {
            Fill(new Rect(292, 913, 1016, 53), Panel);
            var own = t.players[t.yourSeat];
            bool animationsDone = Time.unscaledTime >= handStarted + InitialDuration(t) && shownBoard == t.board.Length;
            bool enabled = network.Connected && !network.CommandPending && !own.leaving && animationsDone;
            if (!own.inHand || t.stage == "Waiting")
            {
                Label(313, 926, 440, 36, own.status, small);
                if (Button(new Rect(767, 921, 240, 36), "Post big blind", enabled)) network.Send(new Command { type = "entry", entryMode = "post" });
                if (Button(new Rect(1019, 921, 269, 36), "Wait for big blind", enabled)) network.Send(new Command { type = "entry", entryMode = "wait" }); return;
            }
            if (t.stage == "Complete") { Label(313, 928, 954, 31, "Next hand starts automatically when two players are available.", small); return; }
            if (t.actor != t.yourSeat || own.folded || own.leaving) { Label(313, 928, 954, 31, own.leaving ? "You will leave after this hand. Your remaining turns fold automatically." : animationsDone ? "Waiting for the other players…" : "Dealing cards…", small); return; }
            if (t.stage == "Draw")
            {
                int count = selected.Count(s => s); Label(313, 928, 590, 30, "Select up to two cards above.  " + count + "/2 selected.", small);
                if (Button(new Rect(991, 921, 297, 36), count == 0 ? "Stand pat" : "Draw " + count + " cards", enabled, true))
                { network.Send(new Command { type = "draw", discards = Enumerable.Range(0, 5).Where(i => selected[i]).ToArray() }); Array.Clear(selected, 0, 5); } return;
            }
            if (Button(new Rect(308, 921, 110, 36), "Fold", enabled)) network.Send(new Command { type = "fold" });
            if (Button(new Rect(430, 921, 159, 36), t.call == 0 ? "Check" : "Call " + t.call, enabled, true)) network.Send(new Command { type = "call" });
            int min = Math.Min(t.minRaise, t.maxRaise), max = t.maxRaise;
            if (t.canRaise)
            {
                raiseTo = Mathf.Clamp(raiseTo, min, max);
                Label(609, 916, 173, 24, "Raise to " + Mathf.RoundToInt(raiseTo), Style(13, Dim));
                raiseTo = Mathf.Clamp(GUI.HorizontalSlider(new Rect(609, 947, 173, 14), raiseTo, min, Math.Max(min + 1, max)), min, max);
            }
            if (Button(new Rect(805, 921, 94, 36), "Min", enabled && t.canRaise)) raiseTo = min;
            if (Button(new Rect(911, 921, 94, 36), "Pot", enabled && t.canRaise)) raiseTo = max;
            if (Button(new Rect(1017, 921, 271, 36), "Raise " + Mathf.RoundToInt(raiseTo), enabled && t.canRaise, true)) network.Send(new Command { type = "raise", raiseTo = Mathf.RoundToInt(raiseTo) });
        }
        void Overlay(string heading)
        {
            Fill(new Rect(0, 0, W, H), new Color(0, 0, 0, 0.85f)); Fill(new Rect(350, 160, 900, 680), Panel); Label(394, 193, 805, 55, heading, title);
        }
        void Rules()
        {
            Overlay("TWO HANDS. ONE POT.");
            Label(394, 270, 805, 475,
                "Five private cards. Pot-limit betting before the flop, after the flop, after the turn, and after the river.\n\n" +
                "One draw of 0–2 cards AFTER flop betting. Replacements arrive immediately. The final cards count for BOTH halves.\n\n" +
                "OMAHA HIGH: exactly two private cards and three board cards.\n\nDRAW HIGH: all five final private cards.\n\n" +
                "Side pots are scored separately. Ties split each half. The Omaha half gets the odd chip; tied odd chips go clockwise from the button.\n\n" +
                "TABLE ENTRY: post a big blind in the next eligible position, or wait for your natural big blind. Never enter an already dealt hand. Entry is blocked on the button and between the button and big blind.\n\n" +
                "Turn timeout: check if free, otherwise fold; stand pat during the draw. Your seat is reserved for 90 seconds if disconnected.", text);
            if (Button(new Rect(964, 773, 241, 44), "Back to game", true, true)) showRules = false;
        }
        void History()
        {
            var state = CurrentTable;
            Overlay("TABLE HISTORY"); var lines = state != null ? state.history : new string[0];
            historyScroll = GUI.BeginScrollView(new Rect(394, 270, 811, 480), historyScroll, new Rect(0, 0, 777, Math.Max(460, lines.Length * 46)));
            for (int i = 0; i < lines.Length; i++) Label(0, i * 46, 764, 44, lines[i], small);
            GUI.EndScrollView(); if (Button(new Rect(964, 773, 241, 44), "Back to game", true, true)) showHistory = false;
        }
        void Settings()
        {
            Overlay("MAKE IT YOUR TABLE");
            Label(394, 277, 790, 66, "Background, table, and card styles are independent. Add theme assets to customize their textures and colors.", small);
            Label(394, 379, 250, 34, "BACKGROUND", bold);
            if (Button(new Rect(685, 368, 520, 48), BackgroundTheme.DisplayName + "   ›")) backgroundIndex = (backgroundIndex + 1) % themes.Length;
            Label(394, 477, 250, 34, "TABLE", bold);
            if (Button(new Rect(685, 466, 520, 48), Theme.DisplayName + "   ›")) { tableIndex = (tableIndex + 1) % themes.Length; RebuildFelt(); }
            Label(394, 575, 250, 34, "CARDS", bold);
            if (Button(new Rect(685, 564, 520, 48), Cards.DisplayName + "   ›")) cardsIndex = (cardsIndex + 1) % themes.Length;
            Label(394, 665, 810, 60, "Profile pictures come from the selected theme's Avatars array. Otherwise each player gets a colored circle with their initial.", small);
            if (Button(new Rect(964, 773, 241, 44), "Save appearance", true, true))
            {
                PlayerPrefs.SetInt("drawmaha.background", backgroundIndex); PlayerPrefs.SetInt("drawmaha.table", tableIndex); PlayerPrefs.SetInt("drawmaha.cards", cardsIndex); PlayerPrefs.Save(); showSettings = false;
            }
        }
    }
}
