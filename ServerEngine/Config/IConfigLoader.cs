﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

using ServerEngine.Network.SystemLib;
using ServerEngine.Common;

namespace ServerEngine.Config
{
    public interface IConfigLoader<ConfigInfo> where ConfigInfo : ServerConfig, new()
    {
        void Initialize(string fileName, eFileExtension fileExtension = eFileExtension.INI);
        
        bool LoadConfig(out ConfigInfo item);

        bool LoadListeners(List<IListenInfo> listeners);
      
    }

}
