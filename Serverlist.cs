namespace Crysis2_MP_Launcher
{
	public class Serverlist
	{
		public string Hostname { get; set; }

		public string Players => $"{Numplayers}/{Maxplayers}";

		public int Numplayers { get; set; }

		public int Maxplayers { get; set; }

		public string Gamemode { get; set; }

		public string Country { get; set; }

		public string Mapname { get; set; }
	}
}
