using System;
using System.Collections;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace SmartTooling
{
    public enum ToolingMode
    {
        /// <summary>Seleciona os itens e ferramentas para construir / avancar a estrutura apontada.</summary>
        Construction,

        /// <summary>Seleciona a ferramenta que desconstroi a estrutura apontada.</summary>
        Deconstruction
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "mooka.stationeers.smarttooling";
        public const string PluginName = "SmartTooling";
        public const string PluginVersion = "1.0.0";

        public static ManualLogSource Log;
        public static Plugin Instance;

        public static ConfigEntry<KeyboardShortcut> SelectKey;
        public static ConfigEntry<KeyboardShortcut> ToggleModeKey;
        public static ConfigEntry<KeyboardShortcut> RestoreKey;
        public static ConfigEntry<ToolingMode> Mode;
        public static ConfigEntry<bool> ShowMessages;
        public static ConfigEntry<bool> ShowModeIndicator;
        public static ConfigEntry<float> MessageSeconds;
        public static ConfigEntry<bool> PlaySounds;

        private static string _message = "";
        private static bool _messageOk;
        private static float _messageUntil;
        private GUIStyle _style;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            SelectKey = Config.Bind("Hotkeys", "Select", new KeyboardShortcut(KeyCode.X),
                "Puts the items/tools needed for the structure you are looking at into your hands (according to the current mode).");
            ToggleModeKey = Config.Bind("Hotkeys", "ToggleMode", new KeyboardShortcut(KeyCode.X, KeyCode.LeftShift),
                "Switches between Construction and Deconstruction mode.");
            RestoreKey = Config.Bind("Hotkeys", "PutBack", new KeyboardShortcut(KeyCode.X, KeyCode.LeftControl),
                "Puts the items moved by the mod back where they were.");
            Mode = Config.Bind("General", "Mode", ToolingMode.Construction,
                "Current mode. Changed in game with the ToggleMode hotkey.");
            ShowMessages = Config.Bind("Display", "ShowMessages", true,
                "Shows a short on-screen message with the result of each hotkey.");
            ShowModeIndicator = Config.Bind("Display", "ShowModeIndicator", false,
                "Always shows the current mode on screen.");
            MessageSeconds = Config.Bind("Display", "MessageSeconds", 2.5f,
                "How long on-screen messages stay visible.");
            PlaySounds = Config.Bind("Display", "PlaySounds", true,
                "Plays the game's inventory sounds when items are moved or an action fails.");

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        // ------------------------------------------------------------------ teclas

        private static bool Held(KeyCode key)
        {
            // Aceita a tecla equivalente do outro lado do teclado (Shift/Ctrl/Alt esquerdo ou direito).
            switch (key)
            {
                case KeyCode.LeftShift:
                case KeyCode.RightShift:
                    return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                case KeyCode.LeftControl:
                case KeyCode.RightControl:
                    return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt:
                    return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                default:
                    return Input.GetKey(key);
            }
        }

        /// <summary>Numero de modificadores do atalho se ele acabou de ser pressionado; -1 caso contrario.</summary>
        private static int Pressed(ConfigEntry<KeyboardShortcut> entry)
        {
            KeyboardShortcut shortcut = entry.Value;
            if (shortcut.MainKey == KeyCode.None || !Input.GetKeyDown(shortcut.MainKey))
            {
                return -1;
            }
            int count = 0;
            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                if (!Held(modifier))
                {
                    return -1;
                }
                count++;
            }
            return count;
        }

        private void Update()
        {
            try
            {
#if SELFTEST
                SelfTest.Tick();
#endif
                if (GameManager.GameState != GameState.Running || GameManager.IsBatchMode)
                {
                    if (GameManager.GameState != GameState.Paused)
                    {
                        Equipper.Reset();
                    }
                    return;
                }
                if (KeyManager.InputState != KeyInputState.Game || InventoryManager.Instance == null || !InventoryManager.ParentHuman)
                {
                    return;
                }

                // Quando varios atalhos usam a mesma tecla (X, Shift+X, Ctrl+X), vale o que tem mais modificadores.
                int select = Pressed(SelectKey);
                int toggle = Pressed(ToggleModeKey);
                int restore = Pressed(RestoreKey);
                int best = Math.Max(select, Math.Max(toggle, restore));
                if (best < 0)
                {
                    return;
                }
                if (toggle == best)
                {
                    ToggleMode();
                }
                else if (restore == best)
                {
                    RunRestore();
                }
                else
                {
                    RunSelect();
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"Error in Update: {ex}");
            }
        }

        // ------------------------------------------------------------------ acoes

        public static void ToggleMode()
        {
            Mode.Value = Mode.Value == ToolingMode.Construction ? ToolingMode.Deconstruction : ToolingMode.Construction;
            Notify("Mode: " + Mode.Value, true, true);
        }

        public static Structure TargetStructure()
        {
            CursorManager cursor = CursorManager.Instance;
            return cursor ? cursor.FoundThing as Structure : null;
        }

        public void RunSelect()
        {
            RunSelect(TargetStructure());
        }

        public void RunSelect(Structure target)
        {
            if (Equipper.Busy)
            {
                return;
            }
            Plan plan = Resolver.Resolve(target, Mode.Value);
            if (plan.Error != null)
            {
                Equipper.Report(false, plan.Error);
                return;
            }
            StartCoroutine(Guarded(Equipper.Equip(plan)));
        }

        public void RunRestore()
        {
            if (!Equipper.Busy)
            {
                StartCoroutine(Guarded(Equipper.Restore()));
            }
        }

        /// <summary>Roda a corrotina registrando qualquer excecao (o Unity as engoliria em silencio no log do jogo).</summary>
        private static IEnumerator Guarded(IEnumerator routine)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!routine.MoveNext())
                    {
                        yield break;
                    }
                    current = routine.Current;
                }
                catch (Exception ex)
                {
                    Log.LogError($"Error while moving items: {ex}");
                    Equipper.Reset();
                    yield break;
                }
                // Corrotinas aninhadas sao percorridas aqui mesmo, para tambem ficarem protegidas.
                if (current is IEnumerator nested)
                {
                    IEnumerator inner = Guarded(nested);
                    while (inner.MoveNext())
                    {
                        yield return inner.Current;
                    }
                }
                else
                {
                    yield return current;
                }
            }
        }

        // ------------------------------------------------------------------ mensagens na tela

        public static void Notify(string message, bool ok, bool sound)
        {
            _message = message ?? "";
            _messageOk = ok;
            _messageUntil = Time.unscaledTime + Mathf.Max(0.5f, MessageSeconds.Value);
            Log.LogDebug(message);
            if (sound && PlaySounds.Value && !GameManager.IsBatchMode)
            {
                try
                {
                    UIAudioManager.Play(ok ? UIAudioManager.ObjectIntoHandHash : UIAudioManager.ActionFailHash);
                }
                catch (Exception)
                {
                    // som e opcional
                }
            }
        }

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || GameManager.GameState != GameState.Running || !InventoryManager.ShowUi)
            {
                return;
            }
            bool showMessage = ShowMessages.Value && Time.unscaledTime < _messageUntil && _message.Length > 0;
            if (!showMessage && !ShowModeIndicator.Value)
            {
                return;
            }
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    richText = false
                };
            }
            _style.fontSize = Mathf.Max(14, Screen.height / 55);
            float width = Screen.width * 0.6f;
            float x = (Screen.width - width) / 2f;
            if (showMessage)
            {
                DrawText(new Rect(x, Screen.height * 0.70f, width, _style.fontSize * 2f), _message,
                    _messageOk ? new Color(0.55f, 1f, 0.6f) : new Color(1f, 0.6f, 0.45f));
            }
            if (ShowModeIndicator.Value)
            {
                DrawText(new Rect(x, Screen.height * 0.70f + _style.fontSize * 1.8f, width, _style.fontSize * 2f),
                    "Smart Tooling: " + Mode.Value, new Color(0.85f, 0.85f, 0.85f));
            }
        }

        private void DrawText(Rect rect, string text, Color color)
        {
            Rect shadow = rect;
            shadow.x += 1.5f;
            shadow.y += 1.5f;
            _style.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(shadow, text, _style);
            _style.normal.textColor = color;
            GUI.Label(rect, text, _style);
        }
    }
}
