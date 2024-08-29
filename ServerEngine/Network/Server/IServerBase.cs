﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using ServerEngine.Log;

namespace ServerEngine.Network.Server
{
    /// <summary>
    /// Server 객체라면 모두 구현해야되는 인터페이스, 인터페이스는 최대한 필요한 부분만 구현. 기능 확장은 확장메서드를 이용
    /// [RelayServer], [AuthServer], [LogicServer], [GuildServer], [DBServer] 등...
    /// </summary>
    public interface IServerBase
    {
        /// <summary>
        /// Logger
        /// </summary>
        ILogger? Logger { get; }

        /// <summary>
        /// 서버 이름
        /// </summary>
        string? name { get; }
    }

}
