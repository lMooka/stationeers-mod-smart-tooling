#if SELFTEST
// Autoteste de desenvolvimento. So entra na DLL quando compilada com -p:SelfTest=true e so roda se existir
// o arquivo %TEMP%\smarttooling_selftest.flag. Abre um mundo novo descartavel, cria uma estrutura e itens,
// executa as acoes do mod, registra o resultado no log e fecha o jogo.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using UnityEngine;

namespace SmartTooling
{
    public static class SelfTest
    {
        private static readonly bool Enabled = File.Exists(Path.Combine(Path.GetTempPath(), "smarttooling_selftest.flag"));
        private static float _nextStatus;
        private static bool _started;
        private static float _runAt = -1f;
        private static bool _running;
        private static int _pass;
        private static int _fail;

        public static void Tick()
        {
            if (!Enabled || _running)
            {
                return;
            }
            if (Time.unscaledTime >= _nextStatus)
            {
                _nextStatus = Time.unscaledTime + 10f;
                Plugin.Log.LogInfo($"SELFTEST status state={GameManager.GameState} init={GameManager.IsInitialized} human={(object)Human.LocalHuman != null} t={Time.unscaledTime:0}");
            }
            if (GameManager.GameState != GameState.Running || (object)Human.LocalHuman == null)
            {
                _runAt = -1f;
                if (!_started && GameManager.IsInitialized && GameManager.GameState == GameState.None && Time.unscaledTime > 25f)
                {
                    _started = true;
                    StartWorld();
                }
                return;
            }
            if (_runAt < 0f)
            {
                _runAt = Time.unscaledTime + 10f;
                return;
            }
            if (Time.unscaledTime < _runAt)
            {
                return;
            }
            _running = true;
            Plugin.Instance.StartCoroutine(RunAll());
        }

        private static async void StartWorld()
        {
            try
            {
                WorldSetting world = WorldSetting.AllWorldSettings.FirstOrDefault(w => !w.IsTutorial && !w.IsDeprecated && w.Id.IndexOf("Lunar", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? WorldSetting.AllWorldSettings.FirstOrDefault(w => !w.IsTutorial && !w.IsDeprecated);
                DifficultySetting difficulty = DifficultySetting.Find("Normal") ?? DifficultySetting.AllSettings.FirstOrDefault();
                StartConditionData start = world.Data.StartConditionDatas.FirstOrDefault(s => s.IsDefault) ?? world.Data.StartConditionDatas.FirstOrDefault();
                Plugin.Log.LogInfo($"SELFTEST iniciando mundo={world.Id} dificuldade={difficulty?.Id} inicio={start?.Id}");
                WorldSetting.SetCurrent(world, start);
                DifficultySetting.SetCurrent(difficulty);
                Assets.Scripts.UI.ImGuiLoadingScreen.WorldName = world.Id;
                await World.StartNewWorld(world.Id);
                Plugin.Log.LogInfo("SELFTEST StartNewWorld retornou");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"SELFTEST falha ao iniciar mundo: {ex}");
            }
        }

        private static void Check(string name, bool ok, string detail = "")
        {
            if (ok) _pass++; else _fail++;
            Plugin.Log.LogInfo($"SELFTEST {(ok ? "PASS" : "FAIL")} {name} {detail}");
        }

        private static string Name(DynamicThing thing)
        {
            return thing ? thing.name + (thing is Stackable s ? " x" + s.Quantity : "") : "vazio";
        }

        private static string Hands()
        {
            InventoryManager inv = InventoryManager.Instance;
            return $"ativa={Name(inv.ActiveHand.Slot.Get())} outra={Name(inv.InactiveHand.Slot.Get())}";
        }

        private static IEnumerator RunAll()
        {
            IEnumerator body = Run();
            while (true)
            {
                object current;
                try
                {
                    if (!body.MoveNext())
                    {
                        break;
                    }
                    current = body.Current;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"SELFTEST EXCEPTION: {ex}");
                    _fail++;
                    break;
                }
                yield return current;
            }
            Plugin.Log.LogInfo($"SELFTEST DONE pass={_pass} fail={_fail}");
            yield return new WaitForSecondsRealtime(2f);
            Application.Quit();
        }

        private static IEnumerator WaitIdle()
        {
            yield return null;
            float deadline = Time.unscaledTime + 8f;
            while (Equipper.Busy && Time.unscaledTime < deadline)
            {
                yield return null;
            }
            yield return null;
        }

        private static Item Give(Item prefab, Human human, List<Item> given)
        {
            if (!prefab)
            {
                return null;
            }
            List<Slot> storage = Equipper.StorageSlots(human);
            Slot slot = Equipper.FindFreeSlot(prefab, storage) ?? storage.FirstOrDefault(s => !s.Get() && s.Type == Slot.Class.None);
            if (slot == null)
            {
                Plugin.Log.LogInfo($"SELFTEST sem slot livre para {prefab.name}");
                return null;
            }
            Item item = OnServer.Create<DynamicThing>(prefab, slot) as Item;
            if (item is Stackable stack)
            {
                stack.Quantity = stack.MaxQuantity;
            }
            given.Add(item);
            Plugin.Log.LogInfo($"SELFTEST criado {Name(item)} em {slot.Parent.name}[{slot.SlotIndex}] tipo={slot.Type} ok={(item ? item.ParentSlot == slot : false)}");
            return item;
        }

        private static IEnumerator Run()
        {
            Human human = Human.LocalHuman;
            Plugin plugin = Plugin.Instance;
            InventoryManager inv = InventoryManager.Instance;
            Vector3 pos = human.Position;
            Plugin.Log.LogInfo($"SELFTEST start human='{human.DisplayName}' runSim={GameManager.RunSimulation} input={KeyManager.InputState}");

            foreach (Slot slot in human.Slots)
            {
                Plugin.Log.LogInfo($"SELFTEST slot humano [{slot.SlotIndex}] tipo={slot.Type} mao={slot.IsHandSlot} ocupante={Name(slot.Get())}");
            }
            // Mundo descartavel: libera espaco no inventario inicial (que vem cheio).
            foreach (Slot slot in Equipper.StorageSlots(human))
            {
                DynamicThing occupant = slot.Get();
                if (occupant && slot.Type == Slot.Class.None && !(occupant is Tool))
                {
                    OnServer.Destroy(occupant);
                }
            }
            yield return new WaitForSecondsRealtime(0.5f);
            List<Slot> storage = Equipper.StorageSlots(human);
            Plugin.Log.LogInfo("SELFTEST armazenamento: " + string.Join("; ", storage.Select(s => $"{s.Parent.name}[{s.SlotIndex}]:{s.Type}={Name(s.Get())}")));
            Check("inventario: ha slots de armazenamento", storage.Count > 0, $"{storage.Count}");
            Check("inventario: slots internos do traje ficam de fora", storage.All(s => !(s.Parent is Assets.Scripts.Objects.Clothing.SuitBase)));

            // ---------------- estrutura de teste: armacao de aco no primeiro estagio
            MultiConstructor kit = Resources.FindObjectsOfTypeAll<MultiConstructor>()
                .Where(c => c.ReferenceId == 0 && c.Constructables.Count > 0 && c.GetType() == typeof(MultiConstructor))
                .OrderBy(c => c.name == "ItemSteelFrames" ? 0 : 1).FirstOrDefault();
            HashSet<int> before = new HashSet<int>(UnityEngine.Object.FindObjectsOfType<Structure>().Select(s => s.GetInstanceID()));
            Grid3 cell = GridController.World.WorldToLocal(pos + new Vector3(0f, 6f, 0f));
            kit.Construct(cell, Quaternion.identity, 0, null, true, 0UL, 0);
            yield return null;
            Structure frame = UnityEngine.Object.FindObjectsOfType<Structure>().FirstOrDefault(s => !before.Contains(s.GetInstanceID()));
            Check("estrutura criada", frame, frame ? $"{frame.name} estagio={frame.CurrentBuildStateIndex}/{frame.BuildStates.Count - 1}" : "");
            if (!frame)
            {
                yield break;
            }
            for (int i = 0; i < frame.BuildStates.Count; i++)
            {
                ToolUse t = frame.BuildStates[i].Tool;
                Plugin.Log.LogInfo($"SELFTEST estagio {i}: entrada={(t.ToolEntry ? t.ToolEntry.name : "-")} x{t.EntryQuantity} entrada2={(t.ToolEntry2 ? t.ToolEntry2.name : "-")} x{t.EntryQuantity2} saida={(t.ToolExit ? t.ToolExit.name : "-")}");
            }

            // ---------------- sem os itens: avisa o que falta e nao mexe nas maos
            Plugin.Mode.Value = ToolingMode.Construction;
            Plan plan = Resolver.Resolve(frame, ToolingMode.Construction);
            Check("plano de construcao", plan.Error == null && plan.Primary != null, plan.Error ?? $"{plan.Action}: {plan.Primary.Describe()} + {(plan.Secondary != null ? plan.Secondary.Describe() : "-")}");
            if (plan.Error != null)
            {
                yield break;
            }
            DynamicThing hand0 = inv.ActiveHand.Slot.Get();
            DynamicThing hand1 = inv.InactiveHand.Slot.Get();
            Plugin.Log.LogInfo("SELFTEST maos iniciais: " + Hands());
            bool hadAny = Equipper.StorageSlots(human).Any(s => plan.Primary.Matches(s.Get() as Item) || (plan.Secondary != null && plan.Secondary.Matches(s.Get() as Item)));
            if (!hadAny)
            {
                plugin.RunSelect(frame);
                yield return WaitIdle();
                Check("sem itens: avisa o que falta", !Equipper.LastOk && Equipper.LastMessage.Contains("missing"), Equipper.LastMessage);
                Check("sem itens: maos intactas", inv.ActiveHand.Slot.Get() == hand0 && inv.InactiveHand.Slot.Get() == hand1, Hands());
            }

            // ---------------- com os itens no inventario
            List<Item> given = new List<Item>();
            ToolUse nextTool = frame.NextBuildState.Tool;
            ToolUse currentTool = frame.BuildStates[frame.CurrentBuildStateIndex].Tool;
            Item filler = Give(currentTool.ToolExit, human, given); // ferramenta de desconstrucao
            Give(nextTool.ToolEntry, human, given);
            Give(nextTool.ToolEntry2, human, given);
            yield return null;
            Dictionary<DynamicThing, Slot> placed = given.Where(i => i).ToDictionary(i => (DynamicThing)i, i => i.ParentSlot);

            // Mao ativa ocupada por outra coisa: o mod precisa guarda-la antes.
            if (filler && !inv.ActiveHand.Slot.Get())
            {
                OnServer.MoveToSlot(filler, inv.ActiveHand.Slot);
                yield return null;
                placed[filler] = inv.ActiveHand.Slot;
            }
            Dictionary<DynamicThing, Slot> snapshot = new Dictionary<DynamicThing, Slot>(placed);
            foreach (Slot hand in new[] { inv.ActiveHand.Slot, inv.InactiveHand.Slot })
            {
                if (hand.Get()) snapshot[hand.Get()] = hand;
            }
            Plugin.Log.LogInfo("SELFTEST antes de selecionar: " + Hands());

            plugin.RunSelect(frame);
            yield return WaitIdle();
            Item a = inv.ActiveHand.Slot.Get() as Item;
            Item b = inv.InactiveHand.Slot.Get() as Item;
            Check("construcao: selecao ok", Equipper.LastOk, Equipper.LastMessage);
            Check("construcao: item principal na mao ativa", plan.Primary.Matches(a), Hands());
            Check("construcao: item secundario na outra mao", plan.Secondary == null || plan.Secondary.Matches(b), Hands());
            Check("construcao: rastreia o que mexeu", Equipper.TrackedCount > 0, $"{Equipper.TrackedCount}");

            // O jogo aceita o que o mod colocou nas maos?
            Attack attack = new Attack(inv.ActiveHand.Slot, inv.InactiveHand.Slot, frame.Position, frame, 1f, null, false, false);
            Thing.DelayedActionInstance preview = frame.AttackWith(attack, false);
            Check("construcao: predicados do jogo aceitam as maos", nextTool.IsToolEntry(a) && (nextTool.ToolEntry2 == null || nextTool.IsToolEntry2(b)));
            // Um macarico apagado deixa a acao desabilitada; isso e do jogo (o jogador precisa acende-lo).
            bool inoperable = a is Tool heldTool && !heldTool.IsOperable;
            Check("construcao: jogo oferece a acao", preview != null && (!preview.IsDisabled || inoperable),
                preview == null ? "null" : $"{preview.ActionMessage} disabled={preview.IsDisabled} ferramentaInoperante={inoperable}");

            // Repetir nao muda nada.
            plugin.RunSelect(frame);
            yield return WaitIdle();
            Check("construcao: repetir mantem as maos", inv.ActiveHand.Slot.Get() == a && inv.InactiveHand.Slot.Get() == b && Equipper.LastMessage.Contains("already"), Equipper.LastMessage);

            // ---------------- modo desconstrucao
            Plugin.ToggleMode();
            Check("alternar modo", Plugin.Mode.Value == ToolingMode.Deconstruction);
            Plan exitPlan = Resolver.Resolve(frame, ToolingMode.Deconstruction);
            Check("plano de desconstrucao", exitPlan.Error == null, exitPlan.Error ?? exitPlan.Primary.Describe());
            plugin.RunSelect(frame);
            yield return WaitIdle();
            Item d = inv.ActiveHand.Slot.Get() as Item;
            Check("desconstrucao: ferramenta na mao ativa", Equipper.LastOk && exitPlan.Primary != null && exitPlan.Primary.Matches(d), Hands() + " | " + Equipper.LastMessage);
            attack = new Attack(inv.ActiveHand.Slot, inv.InactiveHand.Slot, frame.Position, frame, 1f, null, false, false);
            preview = frame.AttackWith(attack, false);
            Check("desconstrucao: jogo aceita a ferramenta", preview != null && !preview.IsDisabled, preview == null ? "null" : $"{preview.ActionMessage} disabled={preview.IsDisabled}");

            // ---------------- devolver
            plugin.RunRestore();
            yield return WaitIdle();
            bool allBack = snapshot.All(p => p.Key && p.Key.ParentSlot == p.Value);
            Check("devolver: tudo no lugar de origem", Equipper.LastOk && allBack,
                Equipper.LastMessage + " | " + string.Join("; ", snapshot.Where(p => !p.Key || p.Key.ParentSlot != p.Value).Select(p => Name(p.Key))) + " | " + Hands());
            Check("devolver: rastreamento limpo", Equipper.TrackedCount == 0);
            plugin.RunRestore();
            yield return WaitIdle();
            Check("devolver: sem nada para devolver", !Equipper.LastOk && Equipper.LastMessage.Contains("Nothing"), Equipper.LastMessage);

            // ---------------- construir de verdade o ultimo estagio (chave de fenda nao precisa estar "ligada")
            Plugin.ToggleMode();
            frame.CurrentBuildStateIndex = frame.BuildStates.Count - 2;
            ToolUse stage = frame.NextBuildState.Tool;
            Plugin.Log.LogInfo($"SELFTEST ultimo estagio: {stage.ToolEntry?.name} + {stage.ToolEntry2?.name} x{stage.EntryQuantity2}");
            Stackable sheets = Give(stage.ToolEntry2, human, given) as Stackable;
            if (!Equipper.StorageSlots(human).Any(s => stage.IsToolEntry(s.Get() as Item)))
            {
                Give(stage.ToolEntry, human, given);
            }
            yield return null;
            int index = frame.CurrentBuildStateIndex;
            int quantity = sheets ? sheets.Quantity : -1;
            plugin.RunSelect(frame);
            yield return WaitIdle();
            Check("ultimo estagio: selecao ok", Equipper.LastOk, Equipper.LastMessage + " | " + Hands());
            attack = new Attack(inv.ActiveHand.Slot, inv.InactiveHand.Slot, frame.Position, frame, 1f, null, false, false);
            preview = frame.AttackWith(attack, false);
            Check("ultimo estagio: jogo aceita", preview != null && !preview.IsDisabled, preview == null ? "null" : $"{preview.ActionMessage} disabled={preview.IsDisabled}");
            frame.AttackWith(attack, true);
            yield return null;
            Check("ultimo estagio: construido de verdade", frame && frame.CurrentBuildStateIndex == index + 1 && (!sheets || sheets.Quantity == quantity - stage.EntryQuantity2),
                $"estagio={frame.CurrentBuildStateIndex} chapas {quantity}->{(sheets ? sheets.Quantity : -1)}");

            plugin.RunSelect(frame);
            yield return WaitIdle();
            Check("estrutura completa: avisa", !Equipper.LastOk || Equipper.LastMessage.StartsWith("Upgrade") || Equipper.LastMessage.StartsWith("Repair"), Equipper.LastMessage);

            Plugin.ToggleMode();
            plugin.RunSelect(frame);
            yield return WaitIdle();
            ToolUse done = frame.BuildStates[frame.CurrentBuildStateIndex].Tool;
            Check("estrutura completa: ferramenta de desconstrucao", Equipper.LastOk && done.IsToolExit(inv.ActiveHand.Slot.Get() as Item), Equipper.LastMessage + " | " + Hands());
            plugin.RunRestore();
            yield return WaitIdle();
            Check("devolver apos varias selecoes", Equipper.LastOk && Equipper.TrackedCount == 0, Equipper.LastMessage + " | " + Hands());
            Plugin.ToggleMode();

            // ---------------- sem alvo
            plugin.RunSelect(null);
            yield return WaitIdle();
            Check("sem alvo: nao faz nada", !Equipper.Busy);
        }
    }
}
#endif
