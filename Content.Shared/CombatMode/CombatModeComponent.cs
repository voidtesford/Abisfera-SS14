// SPDX-FileCopyrightText: 2020 Tyler Young <tyler.young@impromptu.ninja>
// SPDX-FileCopyrightText: 2021 Acruid <shatter66@gmail.com>
// SPDX-FileCopyrightText: 2021 Metal Gear Sloth <metalgearsloth@gmail.com>
// SPDX-FileCopyrightText: 2021 Paul <ritter.paul1+git@googlemail.com>
// SPDX-FileCopyrightText: 2021 Paul Ritter <ritter.paul1@googlemail.com>
// SPDX-FileCopyrightText: 2021 Pieter-Jan Briers <pieterjan.briers+git@gmail.com>
// SPDX-FileCopyrightText: 2021 Vera Aguilera Puerto <6766154+Zumorica@users.noreply.github.com>
// SPDX-FileCopyrightText: 2021 Vera Aguilera Puerto <zddm@outlook.es>
// SPDX-FileCopyrightText: 2021 Visne <39844191+Visne@users.noreply.github.com>
// SPDX-FileCopyrightText: 2022 Júlio César Ueti <52474532+Mirino97@users.noreply.github.com>
// SPDX-FileCopyrightText: 2022 Rane <60792108+Elijahrane@users.noreply.github.com>
// SPDX-FileCopyrightText: 2022 metalgearsloth <metalgearsloth@gmail.com>
// SPDX-FileCopyrightText: 2022 wrexbe <81056464+wrexbe@users.noreply.github.com>
// SPDX-FileCopyrightText: 2023 DrSmugleaf <DrSmugleaf@users.noreply.github.com>
// SPDX-FileCopyrightText: 2023 Kara <lunarautomaton6@gmail.com>
// SPDX-FileCopyrightText: 2023 Leon Friedrich <60421075+ElectroJr@users.noreply.github.com>
// SPDX-FileCopyrightText: 2023 iacore <74560659+iacore@users.noreply.github.com>
// SPDX-FileCopyrightText: 2023 metalgearsloth <31366439+metalgearsloth@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
//
// SPDX-License-Identifier: MIT

using Content.Shared.MouseRotator;
using Content.Shared.Movement.Components;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype;

namespace Content.Shared.CombatMode
{
    [RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
    [Access(typeof(SharedCombatModeSystem))]
    public sealed partial class CombatModeComponent : Component
    {
        [DataField, AutoNetworkedField]
        public bool IsInCombatMode;

        #region Disarm

        [ViewVariables(VVAccess.ReadWrite), DataField("canDisarm")]
        public bool? CanDisarm;

        [DataField("disarmSuccessSound")]
        public SoundSpecifier DisarmSuccessSound = new SoundPathSpecifier("/Audio/Effects/thudswoosh.ogg");

        [DataField("disarmFailChance")]
        public float BaseDisarmFailChance = 0.75f;

        #endregion

        [DataField("combatToggleAction", customTypeSerializer: typeof(PrototypeIdSerializer<EntityPrototype>))]
        public string CombatToggleAction = "ActionCombatModeToggle";

        [DataField, AutoNetworkedField]
        public EntityUid? CombatToggleActionEntity;
        
        [DataField, AutoNetworkedField]
        public bool ToggleMouseRotator = true;
    }
}