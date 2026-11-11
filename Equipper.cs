using System.Collections;
using System.Collections.Generic;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using UnityEngine;

namespace SmartTooling
{
    /// <summary>
    /// Move itens entre o inventario e as maos. Usa OnServer.MoveToSlot / SwapSlots, os mesmos caminhos
    /// da interface do jogo, entao funciona tanto como host quanto como cliente em multiplayer.
    /// Cada movimento espera o anterior terminar (em cliente a resposta vem do servidor).
    /// </summary>
    internal static class Equipper
    {
        private const float StepTimeout = 1.5f;

        /// <summary>Onde cada item mexido pelo mod estava antes (para o "devolver").</summary>
        private static readonly Dictionary<DynamicThing, Slot> Origins = new Dictionary<DynamicThing, Slot>();

        private static Human _owner;

        public static bool Busy { get; private set; }

        public static int TrackedCount => Origins.Count;

        /// <summary>Resultado da ultima operacao (usado pelo HUD e pelo autoteste).</summary>
        public static string LastMessage { get; private set; } = "";

        public static bool LastOk { get; private set; }

        public static void Reset()
        {
            Origins.Clear();
            _owner = null;
            Busy = false;
        }

        // ------------------------------------------------------------------ inventario

        private static bool IsWearableSlot(Slot.Class type)
        {
            return type == Slot.Class.Back || type == Slot.Class.Belt || type == Slot.Class.Uniform || type == Slot.Class.Suit;
        }

        /// <summary>
        /// Slots de armazenamento do jogador: o conteudo da mochila, cinto e uniforme (inclusive a mochila
        /// presa ao traje). Nao inclui as maos, os slots do corpo nem os slots internos do traje
        /// (cilindros, bateria, filtros), para o mod nunca desmontar o suporte de vida.
        /// </summary>
        private static void CollectStorage(Thing parent, bool yieldSlots, int depth, List<Slot> output)
        {
            if (!parent || parent.Slots == null)
            {
                return;
            }
            foreach (Slot slot in parent.Slots)
            {
                if (slot == null || slot.IsHandSlot)
                {
                    continue;
                }
                bool wearable = IsWearableSlot(slot.Type);
                if (yieldSlots && !wearable && !slot.IsLocked)
                {
                    output.Add(slot);
                }
                DynamicThing occupant = slot.Get();
                if (wearable && occupant && depth < 3)
                {
                    CollectStorage(occupant, slot.Type != Slot.Class.Suit, depth + 1, output);
                }
            }
        }

        internal static List<Slot> StorageSlots(Human human)
        {
            List<Slot> slots = new List<Slot>();
            CollectStorage(human, false, 0, slots);
            return slots;
        }

        private static Item Find(Need need, Human human, Slot preferredHand, Slot otherHand, List<Slot> storage, DynamicThing exclude)
        {
            if (need == null)
            {
                return null;
            }
            Item best = null;
            float bestScore = float.MinValue;
            void Consider(DynamicThing thing, float bonus)
            {
                Item item = thing as Item;
                if (!item || item == exclude || item.IsBeingDestroyed || !need.Matches(item))
                {
                    return;
                }
                float score = bonus;
                if (!(item is Tool tool) || tool.IsOperable)
                {
                    score += 100f;
                }
                if (need.HasEnough(item))
                {
                    score += 50f;
                }
                if (item is Stackable stack)
                {
                    score += Mathf.Clamp01(stack.Quantity / (float)Mathf.Max(1, stack.MaxQuantity));
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }
            Consider(preferredHand.Get(), 20f);
            Consider(otherHand.Get(), 10f);
            foreach (Slot slot in storage)
            {
                Consider(slot.Get(), 0f);
            }
            return best;
        }

        internal static Slot FindFreeSlot(DynamicThing thing, List<Slot> storage)
        {
            Slot generic = null;
            foreach (Slot slot in storage)
            {
                if (!Slot.AllowMove(thing, slot))
                {
                    continue;
                }
                if (slot.Type != Slot.Class.None)
                {
                    return slot; // slot dedicado (ex.: slot de ferramenta do cinto) tem prioridade
                }
                if (generic == null)
                {
                    generic = slot;
                }
            }
            return generic;
        }

        private static void Track(DynamicThing thing, Slot origin)
        {
            if (thing && origin != null && !Origins.ContainsKey(thing))
            {
                Origins[thing] = origin;
            }
        }

        private static void Prune(Human human)
        {
            if (_owner != human)
            {
                Origins.Clear();
                _owner = human;
            }
            List<DynamicThing> dead = null;
            foreach (KeyValuePair<DynamicThing, Slot> pair in Origins)
            {
                DynamicThing thing = pair.Key;
                if (!thing || thing.IsBeingDestroyed || thing.RootParent != human || thing.ParentSlot == pair.Value
                    || pair.Value == null || !pair.Value.Parent || pair.Value.Parent.RootParent != human)
                {
                    (dead ?? (dead = new List<DynamicThing>())).Add(thing);
                }
            }
            if (dead != null)
            {
                foreach (DynamicThing thing in dead)
                {
                    Origins.Remove(thing);
                }
            }
        }

        // ------------------------------------------------------------------ movimentos

        private static IEnumerator Move(DynamicThing thing, Slot target, bool[] ok)
        {
            ok[0] = false;
            if (!thing || target == null)
            {
                yield break;
            }
            if (thing.ParentSlot == target)
            {
                ok[0] = true;
                yield break;
            }
            OnServer.MoveToSlot(thing, target);
            float deadline = Time.unscaledTime + StepTimeout;
            while (thing && thing.ParentSlot != target && Time.unscaledTime < deadline)
            {
                yield return null;
            }
            ok[0] = thing && thing.ParentSlot == target;
        }

        private static IEnumerator Swap(Slot a, Slot b, bool[] ok)
        {
            DynamicThing fromA = a.Get();
            DynamicThing fromB = b.Get();
            OnServer.SwapSlots(a, b);
            float deadline = Time.unscaledTime + StepTimeout;
            while (Time.unscaledTime < deadline && !(a.Get() == fromB && b.Get() == fromA))
            {
                yield return null;
            }
            ok[0] = a.Get() == fromB && b.Get() == fromA;
        }

        /// <summary>Tira das maos o que estiver em <paramref name="hand"/>, de volta para a origem ou um slot livre.</summary>
        private static IEnumerator Stow(Human human, Slot hand, bool[] ok)
        {
            DynamicThing thing = hand.Get();
            if (!thing)
            {
                ok[0] = true;
                yield break;
            }
            List<Slot> storage = StorageSlots(human);
            Slot target = null;
            if (Origins.TryGetValue(thing, out Slot origin) && !origin.IsHandSlot && Slot.AllowMove(thing, origin))
            {
                target = origin;
            }
            if (target == null)
            {
                target = FindFreeSlot(thing, storage);
            }
            if (target == null)
            {
                ok[0] = false;
                yield break;
            }
            Track(thing, hand);
            yield return Move(thing, target, ok);
            if (ok[0] && Origins.TryGetValue(thing, out Slot recorded) && recorded == target)
            {
                Origins.Remove(thing);
            }
        }

        /// <summary>Coloca <paramref name="item"/> em <paramref name="hand"/>, guardando o que estiver la.</summary>
        private static IEnumerator Place(Human human, Item item, Slot hand, bool[] ok)
        {
            if (hand.Get() == item)
            {
                ok[0] = true;
                yield break;
            }
            DynamicThing blocking = hand.Get();
            if (blocking)
            {
                yield return Stow(human, hand, ok);
                if (!ok[0])
                {
                    // Sem slot livre: troca direta, se o slot de origem aceitar o item que esta na mao.
                    Slot source = item.ParentSlot;
                    if (source != null && source.IsAllowedType(blocking))
                    {
                        Track(blocking, hand);
                        Track(item, source);
                        yield return Swap(hand, source, ok);
                    }
                    if (!ok[0])
                    {
                        _failure = "No free slot to stow " + blocking.DisplayName;
                    }
                    yield break;
                }
            }
            Track(item, item.ParentSlot);
            yield return Move(item, hand, ok);
            if (!ok[0])
            {
                _failure = "Could not move " + item.DisplayName;
            }
        }

        private static string _failure;

        // ------------------------------------------------------------------ acoes publicas

        /// <summary>Leva para as maos o que o plano pede. Mao ativa = item principal; outra mao = secundario.</summary>
        public static IEnumerator Equip(Plan plan)
        {
            Busy = true;
            try
            {
                Human human = InventoryManager.ParentHuman;
                InventoryManager inventory = InventoryManager.Instance;
                Slot active = inventory.ActiveHand.Slot;
                Slot inactive = inventory.InactiveHand.Slot;
                Prune(human);

                List<Slot> storage = StorageSlots(human);
                Item primary = Find(plan.Primary, human, active, inactive, storage, null);
                Item secondary = Find(plan.Secondary, human, inactive, active, storage, primary);

                List<string> missing = new List<string>();
                if (plan.Primary != null && !primary) missing.Add(plan.Primary.Describe());
                if (plan.Secondary != null && !secondary) missing.Add(plan.Secondary.Describe());

                bool[] ok = { true };
                _failure = null;
                bool changed = false;

                if (primary || secondary)
                {
                    inventory.CheckCancelMultiConstructor();
                }
                if (primary && secondary && inactive.Get() == primary && active.Get() == secondary)
                {
                    Track(primary, inactive);
                    Track(secondary, active);
                    yield return Swap(active, inactive, ok);
                    changed = true;
                }
                else
                {
                    // Se o secundario esta na mao ativa, ele sai de la primeiro.
                    bool secondaryFirst = secondary && active.Get() == secondary;
                    for (int pass = 0; pass < 2 && ok[0]; pass++)
                    {
                        bool doSecondary = (pass == 0) == secondaryFirst;
                        Item item = doSecondary ? secondary : primary;
                        Slot hand = doSecondary ? inactive : active;
                        if (item && hand.Get() != item)
                        {
                            changed = true;
                            yield return Place(human, item, hand, ok);
                        }
                    }
                }

                string what = plan.Action;
                if (!ok[0])
                {
                    Finish(false, _failure ?? "Could not move the items");
                }
                else if (missing.Count > 0)
                {
                    Finish(false, what + ": missing " + string.Join(" and ", missing.ToArray()));
                }
                else
                {
                    bool enough = plan.Primary.HasEnough(primary) && (plan.Secondary == null || plan.Secondary.HasEnough(secondary));
                    string names = primary.DisplayName + (secondary ? " + " + secondary.DisplayName : "");
                    if (!enough)
                    {
                        Finish(false, what + ": " + names + " (not enough quantity)", sound: changed);
                    }
                    else
                    {
                        Finish(true, what + ": " + names + (changed ? "" : " (already in hand)"), sound: changed);
                    }
                }
            }
            finally
            {
                Busy = false;
            }
        }

        /// <summary>Devolve tudo o que o mod mexeu para onde estava.</summary>
        public static IEnumerator Restore()
        {
            Busy = true;
            try
            {
                Human human = InventoryManager.ParentHuman;
                Prune(human);
                if (Origins.Count == 0)
                {
                    Finish(false, "Nothing to put back");
                    yield break;
                }
                InventoryManager.Instance.CheckCancelMultiConstructor();
                Slot left = InventoryManager.LeftHandSlot;
                Slot right = InventoryManager.RightHandSlot;
                bool[] ok = { true };
                int moved = 0;
                int failed = 0;

                // 1) O que veio do inventario volta para o inventario.
                foreach (KeyValuePair<DynamicThing, Slot> pair in new List<KeyValuePair<DynamicThing, Slot>>(Origins))
                {
                    DynamicThing thing = pair.Key;
                    Slot origin = pair.Value;
                    if (origin.IsHandSlot || !thing)
                    {
                        continue;
                    }
                    Slot target = Slot.AllowMove(thing, origin) ? origin : (thing.ParentSlot != null && thing.ParentSlot.IsHandSlot ? FindFreeSlot(thing, StorageSlots(human)) : null);
                    if (target == null)
                    {
                        failed++;
                        continue;
                    }
                    yield return Move(thing, target, ok);
                    if (ok[0]) moved++; else failed++;
                }

                // 2) O que estava nas maos volta para as maos.
                DynamicThing wantsLeft = null;
                DynamicThing wantsRight = null;
                foreach (KeyValuePair<DynamicThing, Slot> pair in Origins)
                {
                    if (!pair.Key) continue;
                    if (pair.Value == left) wantsLeft = pair.Key;
                    else if (pair.Value == right) wantsRight = pair.Key;
                }
                if (wantsLeft && wantsRight && left.Get() == wantsRight && right.Get() == wantsLeft)
                {
                    yield return Swap(left, right, ok);
                    if (ok[0]) moved += 2; else failed += 2;
                }
                else
                {
                    // Primeiro quem libera uma mao para o outro.
                    bool rightFirst = wantsRight && left.Get() == wantsRight;
                    for (int pass = 0; pass < 2; pass++)
                    {
                        bool doRight = (pass == 0) == rightFirst;
                        DynamicThing thing = doRight ? wantsRight : wantsLeft;
                        Slot hand = doRight ? right : left;
                        if (!thing || hand.Get() == thing)
                        {
                            continue;
                        }
                        if (!Slot.AllowMove(thing, hand))
                        {
                            failed++;
                            continue;
                        }
                        yield return Move(thing, hand, ok);
                        if (ok[0]) moved++; else failed++;
                    }
                }

                Origins.Clear();
                if (failed > 0)
                {
                    Finish(false, moved + " item(s) put back, " + failed + " could not be moved");
                }
                else
                {
                    Finish(true, moved > 0 ? "Items put back" : "Nothing to put back", sound: moved > 0);
                }
            }
            finally
            {
                Busy = false;
            }
        }

        internal static void Report(bool ok, string message)
        {
            Finish(ok, message);
        }

        private static void Finish(bool ok, string message, bool sound = true)
        {
            LastOk = ok;
            LastMessage = message;
            Plugin.Notify(message, ok, sound);
        }
    }
}
