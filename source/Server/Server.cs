using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using SteveNetworking.Common;

namespace SteveNetworking.Server
{
	/// <summary>
	/// UDP server which handles (multiple) connections to clients.
	/// </summary>
	public class Server
	{
		/// <summary>
		/// Maximum amount of packets that are processed per tick.
		/// </summary>
		public int MaxPacketsReceivedPerTick = 5;
		/// <summary>
		/// Event handler for when packets are received and processed.
		/// </summary>
		/// <param name="packet">The packet that was received.</param>
		/// <param name="IPEndPoint">The IP endpoint of the client the packet was received from.</param>
		/// <param name="clientID">The ID of the client the packet was received from. <see langword="null"/> if the client is being (dis)connected.</param>
		public delegate void PacketReceivedEventHandler(Packet packet, IPEndPoint IPEndPoint, int? clientID);
		/// <summary>
		/// Event that is called when packets get received and processed.
		/// </summary>
		public event PacketReceivedEventHandler PacketReceived;
		/// <summary>
		/// The base UDP client class that is built on top of.
		/// </summary>
		public UdpClient UdpClient { get; private set; }
		/// <summary>
		/// The IP endpoint of the local server. <see langword="null"/> when the server isn't started.
		/// </summary>
		public IPEndPoint IPEndPoint { get; private set; }
		/// <summary>
		/// If the server has started. Some variables are <see langword="null"/> if the server isn't started, see the variables' documentation.
		/// </summary>
		public bool HasStarted { get; private set; }
		/// <summary>
		/// If the server is stopping. Some variables are <see langword="null"/> if the server isn't connected to a server, see the variables' documentation.
		/// </summary>
		public bool IsStopping { get; private set; }
		/// <summary>
		/// A dictionary containing all the connected clients, mapped as IP->ID.
		/// </summary>
		public Dictionary<IPEndPoint, int> ConnectedClientsIPToID { get; private set; } = new Dictionary<IPEndPoint, int>();
		/// <summary>
		/// A dictionary containing all the connected clients, mapped as ID->IP.
		/// </summary>
		public Dictionary<int, IPEndPoint> ConnectedClientsIDToIP { get; private set; } = new Dictionary<int, IPEndPoint>();
		/// <summary>
		/// A dictionary containing all the saved clients, mapped as IP->ID
		/// </summary>
		public Dictionary<IPEndPoint, int> SavedClientsIpToID { get; private set; } = new Dictionary<IPEndPoint, int>();
		/// <summary>
		/// A dictionary containing all the saved clients, mapped as ID->IP
		/// </summary>
		public Dictionary<int, IPEndPoint> SavedClientsIDToIp { get; private set; } = new Dictionary<int, IPEndPoint>();
		/// <summary>
		/// The server's logger.
		/// </summary>
		public readonly LogHelper LogHelper = new("[SteveNetworking (Server)]: ");

		private readonly Dictionary<int, List<PacketReceivedEventHandler>> PacketListeners = new();

		/// <summary>
		/// Initialises the server.
		/// </summary>
		public Server()
		{
			PacketReceived += OnPacketReceived;

			Listen((int)DefaultPacketTypes.Connect, OnConnect);
			Listen((int)DefaultPacketTypes.Connect, OnDisconnect);
		}

		/// <summary>
		/// Starts a new instance of the server on the specified port.
		/// </summary>
		/// <param name="port">The port to use for the server.</param>
		public void Start(int port)
		{
			UdpClient = new UdpClient(port);
			IPEndPoint = (IPEndPoint)UdpClient.Client.LocalEndPoint;

			HasStarted = true;
			LogHelper.LogMessage(LogHelper.LogLevel.Info, $"Server started successfully on {IPEndPoint}.");
		}

		/// <summary>
		/// Stops the server instance.
		/// </summary>
		public void Stop()
		{
			try
			{
				if (IsStopping)
				{
					LogHelper.LogMessage(LogHelper.LogLevel.Warning, $"Failed stopping the server: the server is already trying to stop.");
					return;
				}

				IsStopping = true;
				UdpClient.Close();
				IPEndPoint = null;
				ConnectedClientsIDToIP = new Dictionary<int, IPEndPoint>();
				ConnectedClientsIPToID = new Dictionary<IPEndPoint, int>();

				HasStarted = false;
				IsStopping = false;
				LogHelper.LogMessage(LogHelper.LogLevel.Info, $"Server stopped successfully.");
			}
			catch (SocketException e)
			{
				LogHelper.LogMessage(LogHelper.LogLevel.Error, $"Failed stopping the server: {e}");
			}
		}

		/// <summary>
		/// Should be ran every frame, put this in your app's main loop.
		/// </summary>
		public void Tick()
		{
			ReceivePackets();
		}

		/// <summary>
		/// Sends a packet to all clients.
		/// </summary>
		/// <param name="packet">The packet to send.</param>
		public void SendPacketToAll(Packet packet)
		{
			// Get data from the packet
			byte[] packetData = packet.ReturnData();

			// Send the packet to all connected clients
			foreach (IPEndPoint connectedClient in ConnectedClientsIDToIP.Values)
			{
				UdpClient.Send(packetData, packetData.Length, connectedClient);
			}
		}

		/// <summary>
		/// Listens for packets of certain types getting received, and notifies subscribed methods.
		/// </summary>
		/// <param name="packetType">The packet type to listen for.</param>
		/// <param name="method">The method to subscribe with.</param>
		public void Listen(int packetType, PacketReceivedEventHandler method)
		{
			if (!PacketListeners.ContainsKey(packetType))
			{
				var packetListeners = new List<PacketReceivedEventHandler>
				{
					method
				};

				PacketListeners.Add(packetType, packetListeners);
				return;
			}

			PacketListeners[packetType].Add(method);
		}

		/// <summary>
		/// Sends a packet to a client.
		/// </summary>
		/// <param name="packet">The packet to send.</param>
		/// <param name="clientID">The client that the packet should be sent to.</param>
		/// <returns></returns>
		public void SendPacket(Packet packet, int clientID)
		{
			// Get data from the packet
			byte[] packetData = packet.ReturnData();

			// Send the packet to the specified client
			if (ConnectedClientsIDToIP.TryGetValue(clientID, out IPEndPoint connectedClient))
			{
				UdpClient.Send(packetData, packetData.Length, connectedClient);
			}
		}

		/// <summary>
		/// Receives up to MaxPacketsReceivedPerTick asynchronously.
		/// </summary>
		private async void ReceivePackets()
		{
			if (UdpClient == null)
			{
				return;
			}

			for (int i = 0; i < MaxPacketsReceivedPerTick && UdpClient.Available > 0; i++)
			{
				try
				{
					// Extract data from the received packet
					UdpReceiveResult udpReceiveResult = await UdpClient.ReceiveAsync();
					IPEndPoint remoteIPEndPoint = udpReceiveResult.RemoteEndPoint;
					byte[] packetData = udpReceiveResult.Buffer;

					// Create new packet object from the received packet data
					using Packet packet = new(packetData);

					// Check if packet contains header and data
					if (packetData.Length <= 0)
					{
						LogHelper.LogMessage(LogHelper.LogLevel.Warning, $"Received an empty packet of type {packet.Type} (header and data missing).");
					}
					else if (packetData.Length < Packet.HeaderLength)
					{
						LogHelper.LogMessage(LogHelper.LogLevel.Warning, $"Received an empty packet of type {packet.Type} (header incomplete and data missing).");
					}
					else if (packetData.Length == Packet.HeaderLength)
					{
						LogHelper.LogMessage(LogHelper.LogLevel.Warning, $"Received an empty packet of type {packet.Type} (data missing).");
					}

					// Invoke packet received event
					if (PacketReceived != null)
					{
						int? clientID = null;
						if (ConnectedClientsIPToID.ContainsKey(remoteIPEndPoint))
						{
							clientID = ConnectedClientsIPToID[remoteIPEndPoint];
						}

						PacketReceived.Invoke(packet, remoteIPEndPoint, clientID);
					}
				}
				catch (Exception e)
				{
					// TODO: Improve packet receive failure log message
					LogHelper.LogMessage(LogHelper.LogLevel.Error, $"Failed receiving a packet from a client: {e}");
				}
			}
		}

		private void OnPacketReceived(Packet packet, IPEndPoint IPEndPoint, int? clientID)
		{
			foreach (PacketReceivedEventHandler packetReceivedEventHandler in PacketListeners[packet.Type])
			{
				packetReceivedEventHandler.Invoke(packet, IPEndPoint, clientID);
			}
		}

		private void OnConnect(Packet packet, IPEndPoint IPEndPoint, int? clientID)
		{
			// Check if client is already connected
			if (ConnectedClientsIDToIP.ContainsValue(IPEndPoint))
			{
				int alreadyConnectedClientID = ConnectedClientsIPToID[IPEndPoint];
				LogHelper.LogMessage(LogHelper.LogLevel.Warning, $"Client {alreadyConnectedClientID} ({IPEndPoint}) failed to connect: already connected.");
				return;
			}

			// Accept the client's connection request
			clientID = ConnectedClientsIDToIP.Count;
			ConnectedClientsIDToIP.Add((int)clientID, IPEndPoint);
			ConnectedClientsIPToID.Add(IPEndPoint, (int)clientID);

			// Send a packet back to the client
			using (Packet newPacket = new((int)DefaultPacketTypes.Connect))
			{
				// Write the client ID to the packet
				newPacket.Writer.Write((int)clientID);

				SendPacket(newPacket, (int)clientID);
			}

			LogHelper.LogMessage(LogHelper.LogLevel.Info, $"Client {clientID} ({IPEndPoint}) successfully connected.");
		}

		private void OnDisconnect(Packet packet, IPEndPoint IPEndPoint, int? clientID)
		{
			// TODO: Improve checking of connected clients
			// Check if client is already disconnected
			if (!ConnectedClientsIDToIP.ContainsValue(IPEndPoint))
			{
				LogHelper.LogMessage(LogHelper.LogLevel.Warning, $"Client {clientID} ({IPEndPoint}) failed to disconnect: already disconnected.");
				return;
			}

			// Disconnect the client
			ConnectedClientsIDToIP.Remove(ConnectedClientsIPToID[IPEndPoint]);
			ConnectedClientsIPToID.Remove(IPEndPoint);

			LogHelper.LogMessage(LogHelper.LogLevel.Info, $"Client {clientID} ({IPEndPoint}) successfully disconnected.");
		}
	}
}
