﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Net;
using System.Net.Sockets;

using ServerEngine.Common;
using ServerEngine.Log;
using ServerEngine.Network.SystemLib;
using ServerEngine.Network.ServerSession;
using ServerEngine.Config;
using System.Collections.Frozen;
using Microsoft.Extensions.ObjectPool;
using System.Diagnostics.CodeAnalysis;
using ServerEngine.Network.Message;

namespace ServerEngine.Network.Server
{
    /// <summary>
    /// AppServer 위에서 여러대의 SocketServer 구동
    /// 각각의 SocketServer는 Listener 를 가지고 있음. 서버의 모든 작업은 SocketServer에서 진행
    /// </summary>
    /*public class ServerAcceptModule : ServerModuleBase
    {
        public ServerAcceptModule() : base()
        {

        }
    }

    public class ServerConnectModule : ServerModuleBase
    {
        public ServerConnectModule() : base()
        {

        }
    }*/

    public class ServerModule : IServerModule, IAsyncEventCallbackHandler
    {
        public enum eServerState : int
        {
            None = 0,
            Initialized = 1,
            SetupFinished = 2,
            NotStarted = 3,
            Running = 4,
            Stopped = 5,
            Max = 6
        }


        /// <summary>
        /// server_module state
        /// </summary>
        private volatile int mState = (int)eServerState.None;

        /// <summary>
        /// Microsoft.Extensions.ObjectPool
        /// </summary>
        protected DisposableObjectPool<SocketAsyncEventArgs> mSendEventArgsPool, mRecvEventArgsPool;
        private DisposableObjectPool<ClientUserToken> mClientUserTokenPool;
        private RecvStreamPool mRecvStreamPool;
        private SendStreamPool mSendStreamPool;

        #region "2022.05.04 기존 커스텀 ObjectPool -> Microsoft.Extensions.ObjectPool로 변경에 따른 코드 주석처리"
        /*
        public SocketAsyncEventArgsPool mRecvEventPool { get; private set; }
        public SocketAsyncEventArgsPool mSendEventPool { get; private set; }
        */
        #endregion

        /// <summary>
        /// uid generator
        /// </summary>
        private Dictionary<UIDGenerator.eGenerateType, UIDGenerator> mUidGenerators;

    #region property
        /// <summary>
        /// server_module endpoint
        /// </summary>
        public IPEndPoint? LocalEndPoint { get; private set; }

        /// <summary>
        /// 서버모듈이 가지고 있는 Accept / Connect 객체 (1:1 관계)
        /// </summary>
        public List<NetworkSystemBase> Acceptors { get; private set; }

        /// <summary>
        /// 모든 리슨정보
        /// </summary>
        public List<IConfigListen> config_listen_list { get; private set; }
 
        /// <summary>
        /// 각각의 서버모듈이 작동하기 시작한 시작시간 (utc+0)
        /// </summary>
        public int ServiceStartTime { get; protected set; }
   
        /// <summary>
        /// server_module name
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// server_module logger
        /// </summary>
        public Log.ILogger Logger { get; }

        /// <summary>
        /// server_module config
        /// </summary>
        protected IConfigCommon Config { get; private set; }

        /// <summary>
        /// Manage Client Access UserToken
        /// </summary>
        public ClientUserTokenManager ClientUserTokenManager { get; private set; }

        /// <summary>
        /// Get ServerModule State
        /// </summary>
        public int GetState => mState;
    #endregion

        public ServerModule(string name, Log.ILogger logger, IConfigCommon config, IAsyncEventCallbackHandler.AsyncEventCallbackHandler handler, List<NetworkSystemBase> acceptor)
        {
            this.Name = name;
            this.Logger = logger;
            this.Config = config;
            this.config_listen_list = config.config_network.config_listen_list;

            ServiceStartTime = DateTime.UtcNow.ToUnixTime();

            // Microsoft.Extensions.ObjectPool 사용
            // ObjectPool에서 관리하는 최대 풀 객체 수 세팅
            int maximum_retained = 0;
            int? pool_default_size = 0;
            if (!config.config_etc.pools.name.Equals(name, StringComparison.OrdinalIgnoreCase)) 
                throw new ArgumentException(nameof(name));

            // Set SocketAsyncEventArgs ObjectPool
            {
                string target = "socketasynceventargs";
                pool_default_size = config.config_etc.pools.list.FirstOrDefault(e => e.name.Equals(target, StringComparison.OrdinalIgnoreCase))?.default_size;

                foreach(var item in config.config_network.config_listen_list)
                    maximum_retained += item.max_connection;

                maximum_retained = true == pool_default_size.HasValue ? pool_default_size.Value : maximum_retained;

                if (Logger.IsEnableDebug)
                    Logger.Debug($"Debug in ServerModuleBase() - SocketAsyncEventArgs ObjectPool Size = {maximum_retained}");
            }

            // ObjectPool 객체에 callback handler만 선제적으로 등록
            mSendEventArgsPool = new DisposableObjectPool<SocketAsyncEventArgs>(new SocketEventArgsObjectPoolPolicy(handler), maximum_retained);
            mRecvEventArgsPool = new DisposableObjectPool<SocketAsyncEventArgs>(new SocketEventArgsObjectPoolPolicy(handler), maximum_retained);

            mClientUserTokenPool = new DisposableObjectPool<ClientUserToken>(new DefaultPooledObjectPolicy<ClientUserToken>(), maximum_retained);

            // Set SendStream ObjectPool
            {
                string target = "sendstream";
                pool_default_size = config.config_etc.pools.list.FirstOrDefault(e => e.name.Equals(target, StringComparison.OrdinalIgnoreCase))?.default_size;

                mSendStreamPool = new SendStreamPool(config.config_network.max_io_thread_count / 2, pool_default_size ?? Utility.MAX_POOL_DEFAULT_SIZE_COMMON, config.config_network.config_socket.send_buff_size);
            }

            // Set RecvStream ObjectPool
            {
                string target = "recvstream";
                pool_default_size = config.config_etc.pools.list.FirstOrDefault(e => e.name.Equals(target, StringComparison.OrdinalIgnoreCase))?.default_size;

                mRecvStreamPool = new RecvStreamPool(config.config_network.max_io_thread_count / 2, pool_default_size ?? Utility.MAX_POOL_DEFAULT_SIZE_COMMON, config.config_network.config_socket.recv_buff_size);
            }

            // Client에서 접속해서 생성된 UserToken 관리
            ClientUserTokenManager = new ClientUserTokenManager(logger, config.config_network);

            // Create UidGenerator
            mUidGenerators = new Dictionary<UIDGenerator.eGenerateType, UIDGenerator>();

            // Accpetor (tcp / udp)
            Acceptors = acceptor;
        }

        public bool UpdateState(eServerState state)
        {
            var old_state = mState;
            if (old_state == (int)state)
                return true;

            return old_state == Interlocked.Exchange(ref mState, (int)state) ? true : false;
        }

        public virtual bool Initialize()
        {
            var config_listen = Config.config_network.config_listen_list.FirstOrDefault(e => e.name.ToLower().Trim() == Name.ToLower().Trim());
            if (null == config_listen)
            {
                Logger.Error($"Error in ServerModuleBase.Initialize() - IConfigListen is null");
                return false;
            }
            
            LocalEndPoint = ServerHostFinder.GetServerIPAddress(config_listen.port);

            var old_state = (eServerState)mState;
            var new_state = eServerState.Initialized;
            if (false == UpdateState(new_state))
            {
                Logger.Error($"Error in ServerModuleBase.Initialize() - Fail to Update State [{old_state}] -> [{new_state}]");
                return false;
            }

            return true;
        }

        public virtual bool InitializeUidGenerators(int server_gid, int server_index, int max_connection)
        {
            for (int i = 1; i < (int)UIDGenerator.eGenerateType.Max; ++i)
            {
                UIDGenerator.eGenerateType type = (UIDGenerator.eGenerateType)i;
                mUidGenerators.Add(type, new UIDGenerator(type, server_gid, server_index, max_connection));
            }

            return true;
        }

        /*public virtual void Initialize(List<IConfigListen> listen_config_list, IConfigListen config_listen, ServerConfig serverInfo, INetworkSystemBase networkSystem, Log.ILogger logger, Func<Session> creater)
        {
            if (null == listen_config_list)
                throw new ArgumentNullException(nameof(listen_config_list));

            if (config_listen == null)
                throw new ArgumentNullException(nameof(config_listen));

            if (serverInfo == null)
                throw new ArgumentNullException(nameof(serverInfo));

            if (networkSystem == null)
                throw new ArgumentNullException(nameof(networkSystem));

            if (logger == null)
                throw new ArgumentNullException(nameof(logger));

            if (creater == null)
                throw new ArgumentNullException(nameof(creater));

            mListenInfoList = listenInfoList;
            mListenInfo = config_listen;
            ipEndPoint = ServerHostFinder.GetServerIPAddress(config_listen.port);

            mServerInfo = serverInfo;
            
            mNetworkSystem = networkSystem;

            #region "2022.05.04 기존 커스텀 ObjectPool -> Microsoft.Extensions.ObjectPool로 변경에 따른 코드 주석처리"
            
            //mRecvEventPool = new SocketAsyncEventArgsPool(mServerInfo.maxConnectNumber);
            //mSendEventPool = new SocketAsyncEventArgsPool(mServerInfo.maxConnectNumber);
            
            #endregion

            var old_state = mState;
            if (false == UpdateState(eServerState.Initialized))
            {
                logger.Error($"Error in ServerModuleBase.Initialize() - Fail to Update State [{(eServerState)old_state}] -> [{(eServerState)mState}]");
                return;
            }

            // 마지막에 초기화 진행
            mNetworkSystem.Initialize(this, config_listen, serverInfo, this.Logger, creater);
            mNetworkSystem.StopCallback += OnNetworkSystemStopped;
        }
        */

        public virtual void OnNewClientCreateHandler(SocketAsyncEventArgs e, bool client_call)
        {
            // accept 및 connect callback handler에서 해당 메서드를 호출
            // socketasynceventargs를 사용하면 비동기 호출한 대상의 usertoken은 알 수 있음
            // 다만, 현재 서버에서 관리중인 전체 usertoken은 알 방법이 없음. usertoken은 session 객체에 포함될 네트워크 기능을 포함하고 있는 멤버객체가 될 예정 

            // UserToken 을 클라이언트 / 서버용 따로관리
            var last_operation = e.LastOperation;
            if (SocketAsyncOperation.Connect != last_operation || SocketAsyncOperation.Accept != last_operation)
            {
                Logger.Error($"Error in ServerModule.OnNewClientCreateHandler() - SocketAsyncOperation Error. LastOperation = {last_operation}");
                return;
            }

            Socket? new_socket = true == client_call ? e.ConnectSocket : e.AcceptSocket;
            if (null == new_socket)
            {
                Logger.Error($"Error in ServerModule.OnNewClientCreateHandler() - socket is null. client_call = {client_call}");
                return;
            }

            if (e.UserToken?.GetType() == typeof(ClientUserToken))
            {
                // connection request by client
                var token = e.UserToken as ClientUserToken;
                if (null == token)
                {
                    Logger.Error($"Error in ServerModule.OnNewClientCreateHandler() - Fail to UserToken Casting [ClientUserToken]");
                    return;
                }

                if (null == e.ConnectSocket)
                {
                    Logger.Error($"Error in ServerModule.OnNewClientCreateHandler() - ConnectSocket is null");
                    return;
                }

                bool get_token = TryGetUid(UIDGenerator.eGenerateType.UserToken, out var token_id);
                if (false == get_token)
                {
                    Logger.Error($"Error in ServerModule.OnNewClientCreateHandler() - Fail to Get ClientUserTokenId");
                    return;
                }

                try
                {
                    // usertoken processing
                    var client_token = mClientUserTokenPool.Get();

                    var socket = new TcpSocket(new_socket, Logger);
                    socket.SetSocketOption(config_network: Config.config_network);
                    socket.SetConnect(SocketBase.eConnectState.Connected);

                    // receive
                    var recv_stream = mRecvStreamPool.Get();
                    if (null == recv_stream)
                    {
                        recv_stream = new RecvStream(Config.config_network.config_socket.recv_buff_size);
                        Logger.Warn($"Warning in ServerModule.OnNewClientCreateHandler() - RecvStream is created by new allocator");
                    }
                    client_token.Initialize(Logger, Config.config_network, socket, mSendEventArgsPool.Get(), mRecvEventArgsPool.Get(), recv_stream, token_id);

                    ClientUserTokenManager.TryAddUserToken(token_id, client_token);

                    client_token.StartReceive(/*recv_stream*/);
                    
                }
                catch (Exception ex) 
                {
                    Logger.Error($"Exception in ServerModule.OnNewClientCreateHandler() - {ex.Message} - {ex.StackTrace}", ex);
                    return;
                }
            }
            else
            {
                // connection request by server 
                var token = e.UserToken as ServerUserToken;
                if (null == token)
                {
                    Logger.Error($"Error in ServerModule.OnNewClientCreateHandler() - Fail to UserToken Casting [ServerUserToken]");
                    return;
                }

                // Todo : server flow when client[server] connects with server[server]
            }
        }

        public bool TryGetUid(UIDGenerator.eGenerateType type, out long uid)
        {
            if (mUidGenerators.TryGetValue(type, out var generator))
            {
                uid = generator.GetInt64();
                return true;
            }
            else
            {
                uid = 0;
                return false;
            }
        }

        /*public virtual Session NewClientSessionCreate(string sessionID, SocketAsyncEventArgs e, Logger logger, Func<Session> creater, bool isClient)
        {
            if (e.LastOperation != SocketAsyncOperation.Accept && 
                e.LastOperation != SocketAsyncOperation.Connect)
            {
                logger.Error(this.ClassName(), this.MethodName(), "Socket LastOperation must be [Accept] or [Connect]");
                return null;
            }

            var session = creater.Invoke();
            //var socket = e.LastOperation == SocketAsyncOperation.Accept ? e.AcceptSocket : e.ConnectSocket;
            if (e.LastOperation == SocketAsyncOperation.Accept)
                session.Initialize(sessionID, e.AcceptSocket, mServerInfo, logger, mListenInfoList, isClient);
            else
                session.Initialize(sessionID, e.ConnectSocket, mServerInfo, logger, mListenInfoList, isClient);

            //session.Initialize(sessionID, socket, mServerInfo, logger, mListenInfoList, isClient);

            #region "2022.05.04 기존 커스텀 ObjectPool -> Microsoft.Extensions.ObjectPool로 변경에 따른 코드 주석처리"
            
            //if (mRecvEventPool.IsEmpty)
            //{
            //    if (logger.IsWarnEnabled)
            //        logger.Warn(this.ClassName(), this.MethodName(), "[Recv] SocketAsyncEventArgsPool is empty. Increase pool max count");
            //}
            //
            //if (mSendEventPool.IsEmpty)
            //{
            //    if (logger.IsWarnEnabled)
            //        logger.Warn(this.ClassName(), this.MethodName(), "[Send] SocketAsyncEventArgsPool is empty. Increase pool max count");
            //}
            //
            //#endregion

            session.Closed += OnSessionClosed;

            #region "2022.05.04 기존 커스텀 ObjectPool -> Microsoft.Extensions.ObjectPool로 변경에 따른 코드 주석처리"
            
            //session.SetRecvEventByPool(mRecvEventPool.Pop());
            //session.SetSendEventByPool(mSendEventPool.Pop());
            
            #endregion
            session.SetSendEventByPool(mSendEventArgsPool.Get());
            session.SetRecvEventByPool(mRecvEventArgsPool.Get());

            return session;
        }

        public virtual void OnSessionClosed(Session session, eCloseReason reason)
        {
            if (null == session)
            {
                Logger.Error($"Error in ServerModuleBase.OnSessionClosed() - Session is null!!!");
                return;
            }

            session.Closed -= OnSessionClosed;

            session.ClearAllSocketAsyncEvent();

            #region "2022.05.04 기존 커스텀 ObjectPool -> Microsoft.Extensions.ObjectPool로 변경에 따른 코드 주석처리"
            
            //mRecvEventPool.Push(session.mRecvEvent);
            //mSendEventPool.Push(session.mSendEvent);
            
            #endregion
            mSendEventArgsPool.Return(session.mSendEvent);
            mRecvEventArgsPool.Return(session.mRecvEvent);

            mSessionManager.Close(session.mSessionID);
        }

        private void OnNetworkSystemStopped(object? sender, EventArgs e)
        {
            if (Logger.IsEnableDebug)
                Logger.Debug($"[{this.Name}][{ipEndPoint.Address}:{ipEndPoint.Port}] was stopped");
        }
        

        public string GetConnectedServerName(EndPoint endPoint)
        {
            var ipEndPoint = (IPEndPoint)endPoint;
            var listenInfo = mListenInfoList.Find((item) => { return item.port == ipEndPoint.Port; });
            return listenInfo != null ? Name : default(string);    
        }
        */
    }
}
