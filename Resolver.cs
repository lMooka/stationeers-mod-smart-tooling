using System;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;

namespace SmartTooling
{
    /// <summary>Um item que precisa estar em uma das maos.</summary>
    internal sealed class Need
    {
        /// <summary>Prefab exigido pelo jogo (pode ser nulo quando <see cref="Custom"/> e usado).</summary>
        public Item Prefab;

        /// <summary>Quantidade consumida, quando o item e empilhavel.</summary>
        public int Quantity;

        /// <summary>Criterio alternativo, para quando o jogo nao exige um prefab especifico.</summary>
        public Func<Item, bool> Custom;

        public string Label;

        public bool Matches(Item item)
        {
            if (!item)
            {
                return false;
            }
            if (Custom != null)
            {
                return Custom(item);
            }
            if (!Prefab)
            {
                return false;
            }
            // Mesma regra de ToolBasic.IsToolEntry: o proprio item ou um substituto dele (ex.: ferramentas eletricas).
            if (item.PrefabHash == Prefab.PrefabHash)
            {
                return true;
            }
            return (bool)item.ReplacementOf && item.ReplacementOf.PrefabHash == Prefab.PrefabHash;
        }

        public bool HasEnough(Item item)
        {
            return !(item is Stackable stack) || Quantity <= 0 || stack.Quantity >= Quantity;
        }

        public string Describe()
        {
            string name = Label ?? (Prefab ? Prefab.DisplayName : "?");
            return Prefab is Stackable && Quantity > 1 ? Quantity + " x " + name : name;
        }

        public static Need From(Item prefab, int quantity)
        {
            return prefab ? new Need { Prefab = prefab, Quantity = quantity } : null;
        }
    }

    /// <summary>O que deve ir para as maos para agir sobre uma estrutura.</summary>
    internal sealed class Plan
    {
        /// <summary>Nome da acao (Construct, Repair, Upgrade, Deconstruct).</summary>
        public string Action;

        /// <summary>Item da mao ativa (o que "ataca" a estrutura).</summary>
        public Need Primary;

        /// <summary>Item da outra mao (pode ser nulo).</summary>
        public Need Secondary;

        /// <summary>Preenchido quando nao ha nada a fazer.</summary>
        public string Error;

        public static Plan Fail(string error)
        {
            return new Plan { Error = error };
        }
    }

    /// <summary>
    /// Descobre o que a estrutura apontada exige. Espelha a ordem de Structure.AttackWith:
    /// conserto, depois proximo estagio de construcao, depois upgrade; e ToolExit para desconstruir.
    /// </summary>
    internal static class Resolver
    {
        public static Plan Resolve(Structure structure, ToolingMode mode)
        {
            if (!structure)
            {
                return Plan.Fail("Look at a structure first");
            }
            return mode == ToolingMode.Construction ? Construction(structure) : Deconstruction(structure);
        }

        private static Plan Construction(Structure s)
        {
            string name = s.DisplayName;
            if (s.IsBroken)
            {
                return Plan.Fail(name + " is broken - it can only be deconstructed");
            }
            bool damaged = s.DamageState != null && s.DamageState.Total > 0f;
            if (damaged && !s.Indestructable && s.RepairTools != null && s.RepairTools.IsValid() && (bool)s.RepairTools.ToolEntry)
            {
                return FromTools("Repair", s.RepairTools);
            }
            BuildState next = s.NextBuildState;
            if (next?.Tool != null && (bool)next.Tool.ToolEntry)
            {
                if (damaged)
                {
                    return Plan.Fail(name + " is damaged and cannot be repaired");
                }
                return FromTools("Construct", next.Tool);
            }
            ToolBasic upgrade = s.UpgradePrefab?.Prefab ? s.UpgradePrefab.UpgradeTools : null;
            if (upgrade != null && upgrade.IsValid() && (bool)upgrade.ToolEntry)
            {
                return FromTools("Upgrade", upgrade);
            }
            return Plan.Fail(name + " is already fully built");
        }

        private static Plan FromTools(string action, ToolBasic tools)
        {
            return new Plan
            {
                Action = action,
                Primary = Need.From(tools.ToolEntry, tools.EntryQuantity),
                Secondary = Need.From(tools.ToolEntry2, tools.EntryQuantity2)
            };
        }

        private static Plan Deconstruction(Structure s)
        {
            if (s.Indestructable)
            {
                return Plan.Fail(s.DisplayName + " cannot be deconstructed");
            }
            BuildState state = null;
            if (s.IsBroken && s.BrokenBuildStates != null && s.BrokenBuildStates.Count > 0)
            {
                int index = Math.Min(Math.Max(Math.Abs(s.CurrentBuildStateIndex) - 1, 0), s.BrokenBuildStates.Count - 1);
                state = s.BrokenBuildStates[index].BuildState;
            }
            else if (s.CurrentBuildStateIndex >= 0 && s.BuildStates != null && s.CurrentBuildStateIndex < s.BuildStates.Count)
            {
                state = s.BuildStates[s.CurrentBuildStateIndex];
            }
            Item exit = state?.Tool?.ToolExit;
            if (exit)
            {
                return new Plan { Action = "Deconstruct", Primary = Need.From(exit, 0) };
            }
            if (s.CurrentBuildStateIndex < 0 && s.BrokenBuildStates != null && s.BrokenBuildStates.Count > 0)
            {
                // Destrocos sem ferramenta definida: o jogo aceita qualquer ferramenta "IConstructor".
                return new Plan
                {
                    Action = "Deconstruct",
                    Primary = new Need { Custom = item => item is Tool && item is IConstructor, Label = "a deconstruction tool" }
                };
            }
            return Plan.Fail(s.DisplayName + " has no deconstruction tool");
        }
    }
}
