// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Misandry <mary@thughunt.ing>
// SPDX-FileCopyrightText: 2025 gus <august.eymann@gmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.IoC;
using Content.Shared._Goobstation.JoinQueue;
using Robust.Shared.ContentPack;
using Robust.Shared.IoC;

namespace Content.Server._Goobstation.Entry;

public sealed class EntryPoint : GameServer
{
    public override void Init()
    {
        base.Init();

        ServerContentIoC.Register();

        IoCManager.BuildGraph();

        IoCManager.Resolve<IJoinQueueManager>().Initialize();
    }
}
