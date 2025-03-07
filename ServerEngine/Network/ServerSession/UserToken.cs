﻿using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ServerEngine.Common;
using ServerEngine.Config;
using ServerEngine.Network.Message;
using ServerEngine.Network.SystemLib;
using Google.Protobuf;
using System.Collections.Concurrent;
using Serilog.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace ServerEngine.Network.ServerSession
{
    public abstract class UserTokenManager
    {
        protected Log.ILogger Logger;
        protected IConfigNetwork m_config_network;

        protected UserTokenManager(Log.ILogger logger, IConfigNetwork config_network)
        {
            this.Logger = logger;
            m_config_network = config_network;
        }
    }

    /*public class UserTokenManager
    {
        #region Lazy Singletone
        public static readonly Lazy<UserTokenManager> m_instance = new Lazy<UserTokenManager>(() => new UserTokenManager());
        public static UserTokenManager Instance => m_instance.Value;
        private UserTokenManager() {}
        #endregion

        private ConcurrentDictionary<int, List<UserToken>> mThreadUserTokens = new ConcurrentDictionary<int, List<UserToken>>();            // key : thread_index 
        public ConcurrentDictionary<long, UserToken> UserTokens { get; private set; } = new ConcurrentDictionary<long, UserToken>();        // key : uid
        
        private Log.ILogger? Logger;
        private IConfigNetwork? m_config_network;

        public bool Initialize(Log.ILogger logger, IConfigNetwork config_network)
        {
            this.Logger = logger;
            m_config_network = config_network;

            for (var i = 0; i < config_network.max_io_thread_count; ++i) 
                mThreadUserTokens.TryAdd(i, new List<UserToken>(1000));

            return true;
        }

        public bool TryAddUserToken(long uid, UserToken token)
        {
            if (0 >= uid)
                throw new ArgumentException(nameof(uid));

            if (null == token)
                throw new ArgumentNullException(nameof(token));

            if (null == m_config_network) 
                throw new NullReferenceException(nameof(m_config_network));

            var index = (int)(uid % m_config_network.max_io_thread_count);
            mThreadUserTokens[index].Add(token);

            return UserTokens.TryAdd(uid, token);
        }

        public async ValueTask Run(int index)
        {
            if (null == m_config_network)
                throw new NullReferenceException(nameof(m_config_network));

            if (0 > index || m_config_network.max_io_thread_count <= index)
                throw new ArgumentException($"Index {index}");

            while(true)
            {
                foreach(var token in mThreadUserTokens[index])
                {
                    await token.SendAsync();
                }

                Thread.Sleep(10);
            }
        }
    }
    */

    public abstract class UserToken : IDisposable
    {
        public enum eTokenType
        {
            None = 0,
            Client,
            Server
        }

        private IPEndPoint? mLocalEndPoint;
        private IPEndPoint? mRemoteEndPoint;
        private bool mDisposed = false;
        private volatile int mConnected = 0;    // 0: false / 1: true
        private IConfigSocket? mConfigSocket;
        private IConfigNetwork? mConfigNetwork;
        private volatile int mCompleteFlag = 0; // 0: false / 1: true
        // send backup queue
        private ConcurrentQueue<SendStream> mSendBackupQueue = new ConcurrentQueue<SendStream>();
        private Func<SocketAsyncEventArgs?, SocketAsyncEventArgs?, SendStreamPool?, bool>? mRetrieveEvent;
        // heartbeat timer
        private System.Threading.Timer? mBackgroundTimer = null;
        private volatile int mHeartbeatCheckTime;
        private volatile int mLastHeartbeatCheckTime;
        private volatile int mHeartbeatCount;

        #region property
        // user uid
        public string mTokenId { get; protected set; } = string.Empty;
        public eTokenType TokenType { get; protected set; } = eTokenType.None;
        // send queue
        protected Channel<SendStream>? SendQueue { get; private set; }
        // socket
        public SocketBase Socket { get; protected set; }
        public Log.ILogger Logger { get; private set; }

        public SocketAsyncEventArgs? SendAsyncEvent { get; private set; }           // retrieve target
        public SocketAsyncEventArgs? RecvAsyncEvent { get; private set; }           // retrieve target

        // UserToken : 5000, pool_size = 10, send_buffer_size = 4KB > 서버당 204MB
        // send에 사용되는 buffer를 담고있는 stream 객체풀
        public SendStreamPool? SendStreamPool { get; private set; }

        // recv handler
        public RecvMessageHandler? RecvMessageHandler { get; private set; }         // retrieve target
        // packet parser
        public ProtoParser mProtoParser { get; private set; } = new ProtoParser();

        public IPEndPoint? GetLocalEndPoint => mLocalEndPoint;
        public IPEndPoint? GetRemoteEndPoint => mRemoteEndPoint;
        public bool Connected => mConnected == 0? false : true;

        public IConfigNetwork? GetConfigNetwork => mConfigNetwork;
        public IConfigSocket? GetConfigSocket => mConfigSocket;

        #endregion

        protected bool InitializeBase(Log.ILogger logger, IConfigNetwork config_network, SocketBase socket, 
                                      SocketAsyncEventArgs send_event_args, SocketAsyncEventArgs recv_event_args, 
                                      SendStreamPool send_stream_pool, RecvStream recv_stream, 
                                      Func<SocketAsyncEventArgs?, SocketAsyncEventArgs?, SendStreamPool?, bool> retrieve_event)
        {
            this.Socket = socket;
            this.Logger = logger;
            mConfigNetwork = config_network;

            mLocalEndPoint = (IPEndPoint?)socket.GetSocket?.LocalEndPoint;
            mRemoteEndPoint = (IPEndPoint?)socket.GetSocket?.RemoteEndPoint;

            SendAsyncEvent = send_event_args;
            RecvAsyncEvent = recv_event_args;

            mConfigSocket = config_network.config_socket;

            // sending queue에 큐 최대사이즈를 지정하여 생성
            SendQueue = Channel.CreateBounded<SendStream>(capacity: config_network.config_socket.send_queue_size);
            SendStreamPool = send_stream_pool;

            //RecvMessageHandler = new RecvMessageHandler(max_buffer_size: config_socket.recv_buff_size, logger: logger);
            RecvMessageHandler = new RecvMessageHandler(stream: recv_stream, max_buffer_size: config_network.config_socket.recv_buff_size, logger: logger);

            mConnected = 1;

            mRetrieveEvent = retrieve_event;

            // UseToken 생성 후 heartbeat_start_time(sec) 이후 heartbeat check 진행
            mBackgroundTimer = new Timer(HeartbeatCheck, null,
                                         TimeSpan.FromSeconds(config_network.config_socket.heartbeat_start_time),
                                         TimeSpan.FromSeconds(config_network.config_socket.heartbeat_check_time));

            return true;
        }

        #region public_method
        public (string, int) GetRemoteEndPointIPAddress()
        {
            if (null == mRemoteEndPoint)
                return default;

            if (null == mRemoteEndPoint.Address)
                return default;

            return (mRemoteEndPoint.Address.ToString(), mRemoteEndPoint.Port);
        }

        // ThreadPool에서 가져온 임의의 작업자 스레드(백그라운드 스레드)에서 실행
        // 자식 클래스에서 오버라이딩하여 사용할 수도 있으므로 일단 가상함수로 선언
        public virtual void HeartbeatCheck(object? state)
        {
            try
            {
                if (Logger.IsEnableDebug)
                    Logger.Debug($"UserToken >> [{TokenType}:{mTokenId}]. Heartbeat Check Start. CurTime = {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")}");

                if (0 != Interlocked.CompareExchange(ref mHeartbeatCheckTime, 0, 0))
                {
                    // 클라이언트로부터 heartbeat 패킷이 서버로 전달되었고
                    // heartbeat interval이 지나서 체크되어야할 시간일 때.
                    if (mLastHeartbeatCheckTime == Interlocked.CompareExchange(ref mHeartbeatCheckTime, mHeartbeatCheckTime, mLastHeartbeatCheckTime))
                    {
                        // 하트비트 시간이 갱신이 안되었다. 즉, 클라이언트로부터 하트비트 패킷이 제대로 전달이 되지 않았다
                        int increased = Interlocked.Increment(ref mHeartbeatCount);
                        if (null != mConfigSocket && increased >= mConfigSocket.heartbeat_count)
                        {
                            // disconnect session
                            ProcessClose(true);

                            if (Logger.IsEnableDebug)
                            {
                                var (ip, port) = GetRemoteEndPointIPAddress();
                                Logger.Debug($"UserToken >> [{TokenType}:{mTokenId}]. [{ip}:{port}] is disconnected. Heartbeat full check. CurTime = {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")}");
                            }
                        }
                    }
                    else
                    {
                        Interlocked.Exchange(ref mHeartbeatCount, 0);
                    }
                }
            }
            catch (Exception)
            {
                throw;
            }
        }


        // 클라이언트 or 서버로부터 전달받은 heartbeat 패킷 시간
        public void SetHeartbeatCheckTime(int time)
        {
            if (0 != Interlocked.CompareExchange(ref mHeartbeatCheckTime, 0, 0))
                Interlocked.Exchange(ref mLastHeartbeatCheckTime, mHeartbeatCheckTime);

            Interlocked.Exchange(ref mHeartbeatCheckTime, time);
        }

        // protobuf 형식의 메시지를 받아 직렬화한 뒤 동기로 메시지 큐잉
        public virtual bool Send<TMessage>(TMessage message, ushort message_id)
            where TMessage : IMessage
        {
            if (null == SendStreamPool)
            {
                Logger.Error($"Error in UserToken.Send() - SendStreamPool is null");
                return false;
            }

            bool result = false;
            SendStream stream = SendStreamPool.Get();
            try
            {
                if (mProtoParser.TrySerialize(message: message,
                                              message_id: message_id,
                                              stream: stream))
                {
                    result = StartSend(stream);
                }
                else
                {
                    Logger.Error($"Error in UserToken.Send() - Fail to Serialize");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Exception in UserToken.Send() - {ex.Message} - {ex.StackTrace}");
            }
            finally
            {
                if (!result) SendStreamPool.Return(stream);
            }

            return result;
        }

        // protobuf 형식의 메시지를 받아 직렬화한 뒤 비동기로 메시지 큐잉
        public virtual async Task SendAsync<TMessage>(TMessage message, ushort message_id, CancellationToken canel_token) 
            where TMessage : IMessage
        {
            if (null == SendStreamPool)
            {
                Logger.Error($"Error in UserToken.SendAsync() - SendStreamPool is null");
                return;
            }

            bool result = false;
            SendStream stream = SendStreamPool.Get();
            try
            {
                if (mProtoParser.TrySerialize(message: message,
                                              message_id: message_id,        
                                              stream: stream))
                {
                    // I/O 작업으로 인해 대기시간이 길게 발생할 수 있어 반환타입으로 ValueTask -> Task로 수정
                    result = await StartSendAsync(stream: stream, cancel_token: canel_token);
                }
                else
                {
                    Logger.Error($"Error in UserToken.SendAsync() - Fail to Serialize");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Exception in UserToken.SendAsync() - {ex.Message} - {ex.StackTrace}");
            }
            finally
            {
                if (!result) SendStreamPool.Return(stream);
            }
        }

        private void InternalSend(SendStream stream)
        {
            bool internal_result = false;
            try
            {
                if (null == SendQueue)
                    throw new NullReferenceException(nameof(SendQueue));

                internal_result = SendQueue.Writer.TryWrite(item: stream);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Warning in UserToken.InternalSend() - Fail to Channel.Writer.TryWrite(). exception = {ex.Message}." +
                            $"SendStream = {Newtonsoft.Json.JsonConvert.SerializeObject(stream, Newtonsoft.Json.Formatting.Indented)}. stack_trace = {ex.StackTrace}");
            }
            finally
            {
                // Todo: 지속적으로 실패하여 백업 큐에 저장되는 대상에 대한 retry 처리 필요
                if (!internal_result) mSendBackupQueue.Enqueue(item: stream);
            }
        }

        // 여러 스레드에서 호출. 패킷 send 진행 시 큐에 동기로 데이터 추가
        // SocketAsyncEventArgs 비동기 객체를 사용하지 않음
        private bool StartSend(SendStream stream)
        {
            if (null == stream.Buffer.Array)
                throw new ArgumentNullException(nameof(stream.Buffer.Array));

            if (null == SendQueue)
                throw new NullReferenceException(nameof(SendQueue));

            Socket.UpdateState(SocketBase.eSocketState.Sending);

            // mCompleteFlag = false 일 때만 queue에 데이터 추가
            if (0 == Interlocked.CompareExchange(ref mCompleteFlag, 0, 0))
            {
                // 백업 큐에 데이터가 있는지 확인하고 송신 큐에 해당 데이터를 추가
                while (!mSendBackupQueue.IsEmpty)
                {
                    if (mSendBackupQueue.TryDequeue(out var backup_stream))
                        InternalSend(backup_stream);
                }

                // 일반적인 데이터 송신을 위한 송신 큐에 데이터 추가
                InternalSend(stream);
            }
            else
            {
                mSendBackupQueue.Enqueue(stream);
            }
            
            return true;
        }

        private async Task InternalSendAsync(SendStream stream, CancellationToken cancel_token)
        {
            try
            {
                if (null == SendQueue) 
                    throw new NullReferenceException(nameof(SendQueue));

               await SendQueue.Writer.WriteAsync(item: stream, cancellationToken: cancel_token);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Warning in UserToken.InternalSendAsync() - Fail to Channel.Writer.WriteAsync(). exception = {ex.Message}." +
                            $"SendStream = {Newtonsoft.Json.JsonConvert.SerializeObject(stream, Newtonsoft.Json.Formatting.Indented)}. stack_trace = {ex.StackTrace}");

                // 쓰기에 실패하면 백업 큐에 다시 저장
                // Todo: 지속적으로 실패하여 백업 큐에 저장되는 대상에 대한 retry 처리 필요
                mSendBackupQueue.Enqueue(item: stream);
            }
        }

        // 여러 스레드에서 호출. 패킷 send 진행 시 큐에 비동기로 데이터 추가
        // SocketAsyncEventArgs 비동기 객체를 사용하지 않음
        private async Task<bool> StartSendAsync(SendStream stream, CancellationToken cancel_token)
        {
            if (null == stream.Buffer.Array)
                throw new ArgumentNullException(nameof(stream.Buffer.Array));

            if (null == SendQueue)
                throw new NullReferenceException(nameof(SendQueue));

            Socket.UpdateState(SocketBase.eSocketState.Sending);

            // mCompleteFlag = false 일 때만 queue에 데이터 추가
            if (0 == Interlocked.CompareExchange(ref mCompleteFlag, 0, 0))
            {
                // 백업 큐에 데이터가 있는지 확인하고 송신 큐에 해당 데이터를 추가
                while (!mSendBackupQueue.IsEmpty) 
                {
                    if (mSendBackupQueue.TryDequeue(out var backup_stream))
                        await InternalSendAsync(stream: backup_stream, cancel_token: cancel_token);
                }

                // 일반적인 데이터 송신을 위한 송신 큐에 데이터 추가
                await InternalSendAsync(stream: stream, cancel_token: cancel_token);
            }
            else
            {
                // Channl이 아직 생성되지 않은 경우 백업 큐에 데이터를 보관 
                mSendBackupQueue.Enqueue(stream);
            }

            return true;
        }

        // 별도의 패킷 처리 스레드에서 호출. 큐잉된 패킷 데이터들에 대한 실질적인 비동기 send 진행
        // io_thread가 5개일 경우 5개의 스레드에서 각 호출
        public virtual async Task ProcessSendAsync()
        {
            if (!Connected || null == SendQueue || null == SendAsyncEvent)
                return;

            // 해당 메서드가 호출되는 순간 Channel.Writer를 Complete로 변경. 더 이상 queue에 데이터 추가가 안됨
            if (0 == Interlocked.CompareExchange(ref mCompleteFlag, 1, 0))
            {
                try
                {
                    // Channel 닫기
                    SendQueue.Writer.Complete();

                    // 비동기 스트림 읽기. Channel에 데이터가 추가될 때마다 대기하지 않고 데이터를 차례로 읽어온다. 추가 대기 없이 남아있는 데이터를 읽는다
                    // Channel이 이미 닫힌 상태에서 비동기 스트림 데이터를 cpu 제어권을 갖고 즉시 읽어온 뒤 finally 구문이 실행된다
                    await foreach(var item in SendQueue.Reader.ReadAllAsync())
                        SendAsyncEvent.BufferList?.Add(item.Buffer);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Exception in UserToken.ProcessSendAsync() - {ex.Message} - {ex.StackTrace}");
                }
                finally
                {
                    //channel의 complete 호출시 해당 channel을 재사용하는것이 아닌 새로운 channel을 생성하여 이후로직을 처리
                    if (null != mConfigSocket)
                        SendQueue = Channel.CreateBounded<SendStream>(capacity: mConfigSocket.send_buff_size);
                    else
                        SendQueue = Channel.CreateBounded<SendStream>(capacity: Utility.MAX_SEND_BUFFER_SIZE_COMMON);

                    // mCompleteFlag = false로 변경하여 송신 큐에 데이터를 담을 수 있도록 변경
                    Interlocked.Exchange(ref mCompleteFlag, 0);         
                }
            }
            
            // 비동기 송신
            try
            {
                var pending = Socket.GetSocket?.SendAsync(SendAsyncEvent);
                if (false == pending)
                    OnSendCompleteHandler(null, SendAsyncEvent);
            }
            catch (Exception ex)
            {
                Logger.Error($"Exception in UserToken.ProcessSendAsync() - {ex.Message} - {ex.StackTrace}");
            }
        }

        // send callback handler
        public virtual void OnSendCompleteHandler(object? sender, SocketAsyncEventArgs e)
        {
            try
            {
                var socket_error = e.SocketError;
                var bytes_transferred = e.BytesTransferred;

                if (false == AsyncCallbackChecker.CheckCallbackHandler(socket_error, bytes_transferred))
                {
                    Logger.Error($"Error in UserToken.OnSendCompleteHandler() - SocketError = {socket_error}, BytesTransferred = {bytes_transferred}");
                    return;
                }

                // 네트워크 상태이상으로 한번에 보내지지 못한 미처리패킷에 대한 후처리 진행
                if (null != e.BufferList)
                {
                    // e.BytesTransferred(실제 전송한 바이트 수)가 BufferList의 각 사이즈 합계(버퍼리스트 총 바이트 수)보다 작다면 재전송
                    int buffer_bytes_transferred = e.BufferList.Sum(buffer => buffer.Count);

                    if (bytes_transferred < buffer_bytes_transferred)
                    {
                        Logger.Warn($"Warning in UserToken.OnSendCompleteHandler() - Partial send detected. Transferred {bytes_transferred} bytes, expected {buffer_bytes_transferred}");

                        List<ArraySegment<byte>> remaining_buffers = GetRemainingBuffers(buffer_list: e.BufferList, bytesTransferred: bytes_transferred);

                        e.BufferList = remaining_buffers;

                        var pending = Socket.GetSocket?.SendAsync(e);
                        if (false == pending)
                            OnSendCompleteHandler(sender, e);
                    }
                    else
                    {
                        // 모든 데이터가 정상적으로 전달
                        if (Logger.IsEnableDebug)
                            Logger.Debug($"All data sent successfully. Transferred {bytes_transferred} bytes. expected {buffer_bytes_transferred}");
                    }
                }
                else
                {
                    Logger.Error($"Error in UserToken.OnSendCompleteHandler() - Send BufferList is Empty. e.BufferList is null");
                }
            }
            catch (Exception ex) 
            {
                Logger.Error($"Exception in UserToken.OnSendCompleteHandler() - {ex.Message} - {ex.StackTrace}", ex);
            }
        }

        /// <summary>
        /// 잔여 Send 패킷으로 인한 재전송이 필요한 경우, 재전송 버퍼체크 및 반환
        /// </summary>
        /// <param name="buffer_list">e.BufferList</param>
        /// <param name="bytesTransferred">e.bytesTransferred</param>
        /// <returns></returns>
        private List<ArraySegment<byte>> GetRemainingBuffers(IList<ArraySegment<byte>> buffer_list, int bytesTransferred)
        {
            List<ArraySegment<byte>> remaining_buffers = new List<ArraySegment<byte>>();
            int total_transferred = 0;

            // buffer[0] : 14
            // buffer[1] : 17
            // buffer[2] : 20  ---- transferred : 51 / 50
            // buffer[3] : 11
            // buffer[4] : 100

            foreach(var buffer in buffer_list) 
            {
                if (null == buffer.Array)
                    continue;

                if (total_transferred + buffer.Count <= bytesTransferred)
                {
                    total_transferred += buffer.Count;
                    continue;
                }

                int remaining_buffer_count = bytesTransferred - total_transferred;
                if (remaining_buffer_count > 0)
                {
                    // offset : buffer.offset + (이미 전송된 바이트 수)
                    // count : 남이있는 바이트 수 >> 세그먼트의 전체 바이트 수 - (이미 전송된 바이트 수)
                    remaining_buffers.Add(new ArraySegment<byte>(buffer.Array, buffer.Offset + remaining_buffer_count, buffer.Count - remaining_buffer_count));
                }
                else
                {
                    // 아예 전송되지 않은 버퍼의 경우
                    remaining_buffers.Add(buffer);
                }

                total_transferred += buffer.Count;
            }

            return remaining_buffers;
        }

        public virtual void StartReceive(/*RecvStream stream*/)
        {
            if (false == Socket.IsNullSocket())
            {
                Logger.Error($"Error in UserToken.StartReceive() - Socket is null");
                return;
            }

            if (false == Socket.IsConnected())
            {
                Logger.Error($"Error in UserToken.StartReceive() - Socket is not connected");
                return;
            }

            if (null == RecvAsyncEvent)
            {
                Logger.Error($"Error in UserToken.StartReceive() - RecvAsyncEvent is null");
                return;
            }

            Socket.UpdateState(SocketBase.eSocketState.Recving);

            try
            {
                // RecvStreamPool에서 해당 UserToken 전용 RecvStream을 할당
                // session 만료 or socket disconnect 시, Pool에 반환
                var buffer = RecvMessageHandler?.GetBuffer;
                if (buffer.HasValue)
                {
                    RecvAsyncEvent.SetBuffer(buffer: buffer.Value.Array, offset: buffer.Value.Offset, count: buffer.Value.Count);
                    var pending = Socket.GetSocket?.ReceiveAsync(e: RecvAsyncEvent);
                    if (false == pending)
                        OnRecvCompleteHandler(null, e: RecvAsyncEvent);
                }
                else
                {
                    Logger.Error($"Error in UserToken.StartReceive() - RecvAsyncEvent.Buffer Set Error");
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Exception in UserToken.StartReceive() - {ex.Message} - {ex.StackTrace}");
                return;
            }
        }

        // recv callback handler
        public virtual void OnRecvCompleteHandler(object? sender, SocketAsyncEventArgs e)
        {
            var socket_error = e.SocketError;
            var bytes_transferred = e.BytesTransferred;

            if (false == AsyncCallbackChecker.CheckCallbackHandler(socket_error, bytes_transferred))
            {
                Logger.Error($"Error in UserToken.OnRecvCompleteHandler() - SocketError = {socket_error}, BytesTransferred = {bytes_transferred}");
                return;
            }

            if (null == RecvMessageHandler)
            {
                Logger.Error($"Error in UserToken.OnRecvCompleteHandler() - RecvMessageHandler is null");
                return;
            }

            Socket.RemoveState(SocketBase.eSocketState.Recving);

            try
            {
                if (false == RecvMessageHandler.WriteMessage(bytes_transferred))
                {
                    Logger.Error($"Error in UserToken.OnRecvCompleteHandler() - Buffer Write Error");
                    return;
                }

                ArraySegment<byte> recv_buffer;
                var read_result = RecvMessageHandler.TryGetReadBuffer(out recv_buffer);
                if (false == read_result && null != mConfigNetwork)
                {
                    int max_recv_buffer = mConfigNetwork.config_socket.recv_buff_size;
                    RecvMessageHandler.ResetBuffer(new RecvStream(buffer_size: max_recv_buffer), max_recv_buffer);

                    RecvMessageHandler.TryGetReadBuffer(out recv_buffer);

                    Logger.Warn($"Warning in UserToken.OnRecvCompleteHandler() - RecvStream is created by new allocator");
                }

                var process_length = RecvMessageHandler?.ProcessReceive(recv_buffer);
                if (false == process_length.HasValue || 
                    0 > process_length || RecvMessageHandler?.GetHaveToReadSize < process_length)
                {
                    Logger.Error($"Error in UserToken.OnRecvCompleteHandler() - Buffer Processing Error. Read bytes = {process_length}");
                    return; 
                }


                if (false == RecvMessageHandler?.ReadMessage(process_length.Value))
                {
                    Logger.Error($"Error in UserToken.OnRecvCompleteHandler() - Buffer Read Error");
                    return;
                }

                StartReceive();

            }
            catch (Exception ex)
            {
                Logger.Error($"Exception in UserToken.OnRecvCompleteHandler() - {ex.Message} - {ex.StackTrace}");
                return;
            }
        }

        public virtual void ProcessClose(bool force_close)
        {
            try
            {
                // 1. null socket check (null > exit)
                // 2. socket close check (closed or closing > exit)
                // 3. socket connect check (false > exit)
                if (true == Socket.IsNullSocket() || true == Socket.IsClosed() || 
                    false == Socket.IsConnected())
                {
                    return;
                }

                Socket.UpdateState(SocketBase.eSocketState.Closing);

                // connected && not closing, close complete
                bool sending = Socket.CheckState(SocketBase.eSocketState.Sending);
                bool recving = Socket.CheckState(SocketBase.eSocketState.Recving);

                if (sending && recving)
                {
                    // socket sending and recving
                    // Todo : SendQueue에 보내야할 데이터가 남아있을 때 처리작업 필요
                   

                }
                else if (sending)
                {
                    // socket sending
                }
                else if (recving)
                {
                    // socket recving
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Exception in UserToken.ProcessEnd() - {ex.Message} - {ex.StackTrace}");
                return;
            }
        }

        public virtual void Dispose()
        {
            if (mDisposed)
                return;

            // Dispose timer
            mBackgroundTimer?.Dispose();

            // Dispose (close) socket
            Socket.DisconnectSocket();

            // Dispose send/recv socketasynceventargs
            mRetrieveEvent?.Invoke(SendAsyncEvent, RecvAsyncEvent, SendStreamPool);

            Interlocked.Exchange(ref mConnected, 0);

            TokenType = eTokenType.None;

            // Todo : SendQueue에 보내야할 데이터가 남아있을 때 처리작업 필요
            /*if (0 < SendQueue.Reader.Count)
            {
                await SendAsync();
            }
            */

            mDisposed = true;
        }
        #endregion
    }
}
