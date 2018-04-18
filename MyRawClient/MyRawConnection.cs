﻿using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using MyRawClient.Auth;
using MyRawClient.Enumerations;
using MyRawClient.Internal;
using MyRawClient.PacketHandlers;
using MyRawClient.Packets;

namespace MyRawClient
{
    public class MyRawConnection : IDbConnection
    {
        private static readonly CapabilityFlags DefaultCapabilities =
            CapabilityFlags.ClientLongPassword |
            CapabilityFlags.ClientConnectWithDB |
            CapabilityFlags.ClientSecureConnection |
            CapabilityFlags.ClientProtocol41 |
            CapabilityFlags.ClientMultiStatements |
            CapabilityFlags.ClientMultiResults;

        public string ConnectionString { get; set; }
        public string Database { get; private set; }
        public Options Options { get; } = new Options();
        public Result Result { get; protected set; }
        public ConnectionState State { get; protected set; } = ConnectionState.Closed;
        public ServerInfo ServerInfo { get; } = new ServerInfo();

        public int ConnectionTimeout => Options.ConnectTimeout;
        public long LastInsertId => Result?.LastInsertId ?? 0;
        public long RowsAffected => Result?.RowsAffected ?? 0;

        private Stream _stream;
        private TcpClient _client;
        private IPacketHandler _handler;

        public MyRawConnection()
        {
        }

        public MyRawConnection(string connectionString)
        {
            ConnectionString = connectionString;
        }

        ~MyRawConnection()
        {
            Dispose();
        }

        public void Dispose()
        {
            Close();
        }


        // --- Internal methods ---

        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        protected void AssertStateIs(params ConnectionState[] states)
        {
            if (!states.Contains(State))
                throw new InvalidOperationException("Unable to perform operation when connection state is " + State);
        }

        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        protected void AssertStateIsNot(params ConnectionState[] states)
        {
            if (states.Contains(State))
                throw new InvalidOperationException("Unable to perform operation when connection state is " + State);
        }

        protected List<ResultSet> DoQuery(string sql, int capacity = 256)
        {
            AssertStateIs(ConnectionState.Open);

            State = ConnectionState.Executing;

            // Build and send query
            var builder = new PacketBuilder(Options.Encoding);
            builder.AppendInt1((byte)Commands.Query);
            builder.AppendStringFixed(sql);
            _handler.SendPacket(_stream, builder.ToPacket(), true);

            List<ResultSet> results = null;
            do
            {
                // Read response
                var packet = _handler.ReadPacket(_stream);

                // Execute queries with no result set
                if (PacketReader.IsOkPacket(packet))
                {
                    HandleOkPacket(packet);
                    State = ConnectionState.Open;
                    return results;
                }

                // Read column count and initialize reader
                var position = 0;
                var columnCount = (int) PacketReader.ReadIntLengthEncoded(packet, ref position);
                var resultSet = new ResultSet(columnCount, capacity, Options.Encoding);

                // Fetch column definitions
                State = ConnectionState.Fetching;
                for (var i = 0; i < columnCount; i++)
                    resultSet.Fields.Add(FetchColumnDefinition());

                HandleOkPacket(_handler.ReadPacket(_stream));

                // Fetch all data
                for (;;)
                {
                    packet = _handler.ReadPacket(_stream);
                    if (PacketReader.IsOkPacket(packet))
                    {
                        HandleOkPacket(packet);
                        break;
                    }

                    resultSet.AddBuffer(packet);
                }

                if (results == null)
                    results = new List<ResultSet>();
                results.Add(resultSet);
            } while (Result.Status.HasFlag(StatusFlags.MoreResultsExist));

            State = ConnectionState.Open;
            return results;
        }

        private ResultField FetchColumnDefinition()
        {
            var item = new ResultField();
            var packet = _handler.ReadPacket(_stream);
            var position = 0;

            item.Catalog = PacketReader.ReadStringLengthEncoded(packet, ref position, Options.Encoding);
            item.Schema = PacketReader.ReadStringLengthEncoded(packet, ref position, Options.Encoding);
            item.Table = PacketReader.ReadStringLengthEncoded(packet, ref position, Options.Encoding);
            item.OrgTable = PacketReader.ReadStringLengthEncoded(packet, ref position, Options.Encoding);
            item.Name = PacketReader.ReadStringLengthEncoded(packet, ref position, Options.Encoding);
            item.OrgName = PacketReader.ReadStringLengthEncoded(packet, ref position, Options.Encoding);

            PacketReader.ReadIntLengthEncoded(packet, ref position);

            item.CharSet = PacketReader.ReadInt2(packet, ref position);
            item.FieldLength = (int) PacketReader.ReadInt4(packet, ref position);
            item.DataType = (RawFieldType) PacketReader.ReadInt1(packet, ref position);
            item.Flags = (ColumnFlags) PacketReader.ReadInt2(packet, ref position);
            item.Decimals = PacketReader.ReadInt1(packet, ref position);

            item.InternalType = ResultField.DataTypeToInternalType(item.DataType, item.Flags);
            item.FieldType = ResultField.InternalTypeToType(item.InternalType);

            return item;
        }

        protected void HandleOkPacket(byte[] packet)
        {
            Result = Result.Decode(packet, Options.Encoding);
        }

        protected void InitializeConnection()
        {
            var packet = _handler.ReadPacket(_stream);
            var handshake = HandshakeRequest.Decode(packet, Options.Encoding);
            ServerInfo.Capabilities = handshake.Capabilities;
            ServerInfo.CharacterSet = handshake.CharacterSet;
            ServerInfo.ConnectionId = handshake.ConnectionId;
            ServerInfo.ServerVersion = handshake.ServerVersion;

            var mycaps = DefaultCapabilities;
            if (Options.UseCompression)
                mycaps |= CapabilityFlags.ClientCompress;

            _handler.SendPacket(_stream, new HandshakeResponse
            {
                Capabilities = mycaps,
                MaxPacketSize = 65536,
                CharacterSet = 33,
                User = Options.User,
                Database = Options.Database,
                Password = NativePassword.Encrypt(Options.Encoding.GetBytes(Options.Password), handshake.AuthData)
            }.Encode(Options.Encoding), false);

            var response = _handler.ReadPacket(_stream);
            if (PacketReader.IsOkPacket(response))
                HandleOkPacket(response);
            else
                throw new MyRawException("Unrecognized packet in handshake: " + PacketReader.PacketToString(response));

            if (ServerInfo.Capabilities.HasFlag(CapabilityFlags.ClientCompress) && Options.UseCompression)
                _handler = new CompressedPacketHandler(_handler);
        }

        protected void UpdateDatabaseName()
        {
            Database = QueryScalar<string>("select database()");
        }


        // --- Public methods ---

        public IDbTransaction BeginTransaction()
        {
            return BeginTransaction(IsolationLevel.Unspecified);
        }

        public IDbTransaction BeginTransaction(IsolationLevel il)
        {
            return new MyRawTransaction(this);
        }

        public void Close()
        {
            if (_stream != null)
            {
                try
                {
                    _handler.SendPacket(_stream, PacketBuilder.MakeCommand(Commands.Quit), true);
                }
                catch
                {
                    //
                }
            }

            try
            {
                _stream = null;
                _client?.Close();
                _client = null;
            }
            catch
            {
                //
            }

            State = ConnectionState.Closed;
        }

        public void ChangeDatabase(string databaseName)
        {
            Execute("use " + QuoteIdentifier(databaseName));
            UpdateDatabaseName();
        }

        public IDbCommand CreateCommand()
        {
            return new MyRawCommand(this);
        }

        public Result Execute(string sql)
        {
            DoQuery(sql);
            return Result;
        }

        public void Open()
        {
            if (!string.IsNullOrWhiteSpace(ConnectionString))
                Helper.ParseConnectionString(ConnectionString, Options);

            AssertStateIs(ConnectionState.Closed);
            State = ConnectionState.Connecting;
            try
            {
                _client = new TcpClient
                {
                    ReceiveTimeout = Options.ConnectTimeout * 1000,
                    ReceiveBufferSize = 32768
                };
                _client.Connect(Options.Server, Options.Port);
                _stream = new BufferedStream(_client.GetStream(), 16384);
                _handler = new DefaultPacketHandler(Options.Encoding);

                try
                {
                    InitializeConnection();
                    State = ConnectionState.Open;
                    _client.ReceiveTimeout = Options.CommandTimeout * 1000;
                    _client.SendTimeout = Options.CommandTimeout * 1000;

                    UpdateDatabaseName();
                }
                catch (Exception)
                {
                    Close();
                    throw;
                }
            }
            catch (Exception)
            {
                State = ConnectionState.Closed;
                throw;
            }
        }

        public void Ping()
        {
            AssertStateIs(ConnectionState.Open);

            _handler.SendPacket(_stream, PacketBuilder.MakeCommand(Commands.Ping), true);
            HandleOkPacket(_handler.ReadPacket(_stream));
        }

        public ResultSet Query(string sql)
        {
            return DoQuery(sql)?.FirstOrDefault();
        }

        public List<ResultSet> QueryMultiple(string sql)
        {
            return DoQuery(sql);
        }

        public object QueryScalar(string sql)
        {
            var result = DoQuery(sql, 1);
            return result == null || result.First().RowCount == 0 ? null : result.First().GetValue(0, 0);
        }

        public T QueryScalar<T>(string sql)
        {
            var result = DoQuery(sql, 1);
            return result == null || result.First().RowCount == 0 ? default(T) : result.First().Get<T>(0, 0);
        }

        public string QuoteIdentifier(string field) => Helper.QuoteIdentifier(field);

        public string QuoteIdentifier(string table, string field) => Helper.QuoteIdentifier(table, field);

        public string QuoteString(string value) => Helper.QuoteString(value);

        public void ResetConnection()
        {
            AssertStateIs(ConnectionState.Open);

            _handler.SendPacket(_stream, PacketBuilder.MakeCommand(Commands.ResetConnection), true);
            HandleOkPacket(_handler.ReadPacket(_stream));
            State = ConnectionState.Open;
        }
    }
}
