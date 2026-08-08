using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace Spludlow.MameAO
{
	public class Operations
	{
		public static int ProcessOperation(Dictionary<string, string> parameters)
		{
			int exitCode = 0;

			DateTime timeStart = DateTime.Now;

			string operation = parameters["operation"];

			int index = operation.IndexOf("_");
			if (index == -1)
			{
				switch (operation)
				{
					case "snap-machine":
						ValidateRequiredParameters(parameters, new string[] { "source", "target" });
						Snap.ImportSnapMachine(parameters["source"], parameters["target"]);
						break;

					case "snap-software":
						ValidateRequiredParameters(parameters, new string[] { "source", "target" });
						Snap.ImportSnapSoftware(parameters["source"], parameters["target"]);
						break;

					case "snap-index":
						Snap.IndexSnapDirectory(Path.Combine(parameters["directory"]));
						break;

					case "process-phone-home":
						ValidateRequiredParameters(parameters, new string[] { "database", "server", "names" });
						PhoneHome.ProcessPhoneHome(parameters["directory"], parameters["database"], parameters["server"], parameters["names"].Split(',').Select(name => name.Trim()).ToArray());
						break;

					case "approve-phone-home":
						ValidateRequiredParameters(parameters, new string[] { "database" });
						PhoneHome.ApprovePhoneHome(parameters["directory"], parameters["database"]);
						break;

					case "update-pugsys-cheats":
						ValidateRequiredParameters(parameters, new string[] { "server", "names" });
						exitCode = Cheats.UpdateFromPugsy(parameters["directory"], parameters["server"], parameters["names"].Split(',').Select(name => name.Trim()).ToArray());
						break;

					default:
						throw new ApplicationException($"Bad operation: {operation}");
				}
			}
			else
			{
				string coreName = operation.Substring(0, index);
				operation = operation.Substring(index + 1);

				ICore core;
				switch (coreName)
				{
					case "mame":
						core = new CoreMame();
						break;

					case "hbmame":
						core = new CoreHbMame();
						break;

					case "fbneo":
						core = new CoreFbNeo();
						break;

					case "tosec":
						core = new CoreTosec();
						break;

					case "redump":
						core = new CoreRedump();
						break;

					case "no-intro":
						core = new CoreNoIntro();
						break;

					default:
						throw new ApplicationException($"Bad core: {coreName}");
				}

				core.Initialize(parameters["directory"], parameters["version"]);

				switch (operation)
				{
					case "get":
						exitCode = core.Get();
						break;

					case "xml":
						core.Xml();
						break;

					case "json":
						core.Json();
						break;

					case "sqlite":
						core.SQLite();
						break;

					case "msaccess":
						core.MsAccess();
						break;

					case "zips":
						core.Zips();
						break;

					case "mssql":
						ValidateRequiredParameters(parameters, new string[] { "server", "names" });
						core.MSSql(parameters["server"], parameters["names"].Split(',').Select(name => name.Trim()).ToArray());
						break;

					case "mssql-payload":
						ValidateRequiredParameters(parameters, new string[] { "server", "names" });
						core.MSSqlPayload(parameters["server"], parameters["names"].Split(',').Select(name => name.Trim()).ToArray());
						break;

					default:
						throw new ApplicationException($"Bad operation: {operation}");
				}
			}

			TimeSpan timeTook = DateTime.Now - timeStart;

			Console.WriteLine($"Operation '{parameters["operation"]}' took: {Math.Round(timeTook.TotalSeconds, 0)} seconds");

			return exitCode;
		}

		private static void ValidateRequiredParameters(Dictionary<string, string> parameters, string[] required)
		{
			List<string> missing = new List<string>();

			foreach (string name in required)
				if (parameters.ContainsKey(name) == false)
					missing.Add(name);

			if (missing.Count > 0)
				throw new ApplicationException($"This operation requires these parameters '{String.Join(", ", missing)}'.");

		}

		public static void CreateMetaDataTable(SqlConnection connection, string coreName, string version, string info)
		{
			string agent = $"mame-ao/{Globals.AssemblyVersion} (https://github.com/sam-ludlow/mame-ao)";

			string tableName = "_metadata";

			Database.ExecuteNonQuery(connection, $"DROP TABLE IF EXISTS [{tableName}];");

			string[] columnDefs = new string[] {
				$"[{tableName}_id] BIGINT NOT NULL PRIMARY KEY",
				"[dataset] NVARCHAR(1024) NOT NULL",
				"[subset] NVARCHAR(1024) NOT NULL",
				"[version] NVARCHAR(1024) NOT NULL",
				"[info] NVARCHAR(1024) NOT NULL",
				"[processed] DATETIME NOT NULL",
				"[agent] NVARCHAR(1024) NOT NULL",
			};
			string commandText = $"CREATE TABLE [{tableName}] ({String.Join(", ", columnDefs)});";

			Console.WriteLine(commandText);
			Database.ExecuteNonQuery(connection, commandText);

			DataTable table = Database.ExecuteFill(connection, $"SELECT * FROM [{tableName}] WHERE (0 = 1)");
			table.TableName = tableName;

			table.Rows.Add(1L, coreName, "", version, info, DateTime.Now, agent);

			Database.BulkInsert(connection, table);
		}

		public static DataTable MakePayloadDataTable(string tableName, string[] keyNames)
		{
			string[] columnNames = new string[] { "title", "xml", "json", "html" };

			DataTable table = new DataTable(tableName);

			List<DataColumn> pks = new List<DataColumn>();
			foreach (string keyName in keyNames)
				pks.Add(table.Columns.Add(keyName, typeof(string)));

			table.PrimaryKey = pks.ToArray();

			foreach (string columnName in columnNames)
				table.Columns.Add(columnName, typeof(string));

			return table;
		}

		public static void MakeMSSQLPayloadsInsert(SqlConnection connection, DataTable table)
		{
			List<string> columnDefs = new List<string>();
			List<string> pkNames = new List<string>();

			foreach (DataColumn column in table.PrimaryKey)
			{
				int max = 1;
				foreach (DataRow row in table.Rows)
				{
					if (row.IsNull(column) == false)
					{
						int len = ((string)row[column]).Length;
						if (len > max)
							max = len;
					}
				}
				column.MaxLength = max;

				string pkDataType = "VARCHAR";
				if (table.TableName == "game_payload" && column.ColumnName == "game_name")
					pkDataType = "NVARCHAR";

				columnDefs.Add($"[{column.ColumnName}] {pkDataType}({column.MaxLength})");

				pkNames.Add(column.ColumnName);
			}
			foreach (DataColumn column in table.Columns)
			{
				if (pkNames.Contains(column.ColumnName) == true)
					continue;

				switch (Type.GetTypeCode(column.DataType))
				{
					case TypeCode.Int32:
						columnDefs.Add($"[{column.ColumnName}] [int]");
						break;

					case TypeCode.Boolean:
						columnDefs.Add($"[{column.ColumnName}] [bit]");
						break;

					default:
						columnDefs.Add($"[{column.ColumnName}] nvarchar({(column.MaxLength == -1 ? "max" : column.MaxLength.ToString())})");
						break;
				}
			}

			columnDefs.Add($"CONSTRAINT [PK_{table.TableName}] PRIMARY KEY NONCLUSTERED ([{String.Join("], [", pkNames)}])");

			Database.ExecuteNonQuery(connection, $"DROP TABLE IF EXISTS [{table.TableName}];");

			string commandText = $"CREATE TABLE [{table.TableName}] ({String.Join(", ", columnDefs)});";
			Console.WriteLine(commandText);
			Database.ExecuteNonQuery(connection, commandText);

			Database.BulkInsert(connection, table);
		}


		/// <summary>
		/// Downloads all missing assets of the specified type and places them (including artwork/samples).
		/// This is like .fetch but also places the files and handles artwork/samples.
		/// </summary>
		public static void UpdateAssets(string assetType)
		{
			Tools.ConsoleHeading(1, $"Updating {assetType} Assets");
			Console.WriteLine("This will download all missing files and place them with artwork/samples.");
			Console.WriteLine();

			Globals.WorkerTaskReport = Reports.PlaceReportTemplate();

			switch (assetType.ToUpper())
			{
				case "MR":
					UpdateMachineRoms();
					break;
				case "MD":
					UpdateMachineDisks();
					break;
				case "SR":
					UpdateSoftwareRoms();
					break;
				case "SD":
					UpdateSoftwareDisks();
					break;
				default:
					throw new ApplicationException($"Unknown asset type: {assetType}");
			}

			if (Globals.Settings.Options["PlaceReport"] == "Yes")
				Globals.Reports.SaveHtmlReport(Globals.WorkerTaskReport, $"Update Assets {assetType}");

			Console.WriteLine();
			Tools.ConsoleHeading(1, $"Update {assetType} Complete");
		}

		private static void UpdateMachineRoms()
		{
			DataTable machineTable = Database.ExecuteFill(Globals.Core.ConnectionStrings[0], "SELECT machine_id, name, romof, description FROM machine ORDER BY machine.name");
			DataTable romTable = Database.ExecuteFill(Globals.Core.ConnectionStrings[0], "SELECT machine_id, sha1, name, merge FROM rom WHERE sha1 IS NOT NULL");

			int totalMachines = 0;
			int processedMachines = 0;

			for (int pass = 0; pass < 2; ++pass)
			{
				foreach (DataRow machineRow in pass == 0 ? machineTable.Select("romof IS NULL") : machineTable.Select("romof IS NOT NULL"))
				{
					long machine_id = (long)machineRow["machine_id"];
					string machine_name = (string)machineRow["name"];

					int dontHaveCount = 0;

					foreach (DataRow row in romTable.Select("machine_id = " + machine_id))
					{
						string sha1 = (string)row["sha1"];
						if (Globals.RomHashStore.Exists(sha1) == false)
							++dontHaveCount;
					}

					if (dontHaveCount != 0)
					{
						totalMachines++;
						Console.WriteLine($"\u001b[93m[{totalMachines}]\u001b[0m Processing machine: \u001b[96m{machine_name}\u001b[0m (\u001b[91m{dontHaveCount} missing files\u001b[0m)");
						
						// Download and place
						Place.PlaceMachineRoms(Globals.Core, machine_name, true);
						
						// Place artwork and samples
						DataRow machine = Globals.Core.GetMachine(machine_name);
						if (machine != null)
						{
							Globals.Samples.PlaceAssets(Globals.Core.Directory, machine);
							Globals.Artwork.PlaceAssets(Globals.Core.Directory, machine);
						}
						
						processedMachines++;
					}
				}
			}

			Console.WriteLine($"\u001b[92mProcessed {processedMachines} of {totalMachines} machines with missing ROMs.\u001b[0m");
		}

		private static void UpdateMachineDisks()
		{
			DataTable machineTable = Database.ExecuteFill(Globals.Core.ConnectionStrings[0], "SELECT machine_id, name, description FROM machine ORDER BY machine.name");
			DataTable diskTable = Database.ExecuteFill(Globals.Core.ConnectionStrings[0], "SELECT machine_id, sha1, name, merge FROM disk WHERE sha1 IS NOT NULL");

			int totalMachines = 0;
			int processedMachines = 0;

			foreach (DataRow machineRow in machineTable.Rows)
			{
				long machine_id = (long)machineRow["machine_id"];
				string machine_name = (string)machineRow["name"];

				int dontHaveCount = 0;

				foreach (DataRow row in diskTable.Select("machine_id = " + machine_id))
				{
					string sha1 = (string)row["sha1"];
					if (Globals.DiskHashStore.Exists(sha1) == false)
						++dontHaveCount;
				}

				if (dontHaveCount != 0)
				{
					totalMachines++;
					Console.WriteLine($"\u001b[93m[{totalMachines}]\u001b[0m Processing machine: \u001b[96m{machine_name}\u001b[0m (\u001b[91m{dontHaveCount} missing disks\u001b[0m)");
					
					// Download and place
					Place.PlaceMachineDisks(Globals.Core, machine_name, true);
					
					// Place artwork and samples
					DataRow machine = Globals.Core.GetMachine(machine_name);
					if (machine != null)
					{
						Globals.Samples.PlaceAssets(Globals.Core.Directory, machine);
						Globals.Artwork.PlaceAssets(Globals.Core.Directory, machine);
					}
					
					processedMachines++;
				}
			}

			Console.WriteLine($"\u001b[92mProcessed {processedMachines} of {totalMachines} machines with missing disks.\u001b[0m");
		}

		private static void UpdateSoftwareRoms()
		{
			DataTable softwarelistTable = Database.ExecuteFill(Globals.Core.ConnectionStrings[1], "SELECT softwarelist.softwarelist_id, softwarelist.name, softwarelist.description FROM softwarelist ORDER BY softwarelist.name");
			DataTable softwareTable = Database.ExecuteFill(Globals.Core.ConnectionStrings[1], "SELECT software.software_id, software.softwarelist_id, software.name, software.description, software.cloneof FROM software ORDER BY software.name");
			DataTable romTable = Database.ExecuteFill(Globals.Core.ConnectionStrings[1], "SELECT part.software_id, rom.name, rom.sha1 FROM (part INNER JOIN dataarea ON part.part_id = dataarea.part_id) INNER JOIN rom ON dataarea.dataarea_id = rom.dataarea_id WHERE (rom.sha1 IS NOT NULL)");

			int totalSoftware = 0;
			int processedSoftware = 0;

			foreach (DataRow softwarelistRow in softwarelistTable.Rows)
			{
				long softwarelist_id = (long)softwarelistRow["softwarelist_id"];
				string softwarelist_name = (string)softwarelistRow["name"];
				
				foreach (DataRow softwareRow in softwareTable.Select($"softwarelist_id = {softwarelist_id}"))
				{
					int dontHaveCount = 0;

					long software_id = (long)softwareRow["software_id"];
					string software_name = (string)softwareRow["name"];
					
					foreach (DataRow romRow in romTable.Select($"software_id = {software_id}"))
					{
						string sha1 = (string)romRow["sha1"];
						if (Globals.RomHashStore.Exists(sha1) == false)
							++dontHaveCount;
					}

					if (dontHaveCount != 0)
					{
						totalSoftware++;
						Console.WriteLine($"\u001b[93m[{totalSoftware}]\u001b[0m Processing software: \u001b[96m{softwarelist_name}/{software_name}\u001b[0m (\u001b[91m{dontHaveCount} missing files\u001b[0m)");
						
						// Download and place
						Place.PlaceSoftwareRoms(Globals.Core, softwarelistRow, softwareRow, true);
						
						processedSoftware++;
					}
				}
			}

			Console.WriteLine($"\u001b[92mProcessed {processedSoftware} of {totalSoftware} software items with missing ROMs.\u001b[0m");
		}

		private static void UpdateSoftwareDisks()
		{
			List<string> ignoreListNames = new List<string>();
			if (Globals.Config.ContainsKey("SoftwareListSkip") == true)
				ignoreListNames.AddRange(Globals.Config["SoftwareListSkip"].Split(',').Select(item => item.Trim()));

			DataTable softwarelistTable = Database.ExecuteFill(Globals.Core.ConnectionStrings[1], "SELECT softwarelist.softwarelist_id, softwarelist.name, softwarelist.description FROM softwarelist ORDER BY softwarelist.name");
			DataTable softwareTable = Database.ExecuteFill(Globals.Core.ConnectionStrings[1], "SELECT software.software_id, software.softwarelist_id, software.name, software.description, software.cloneof FROM software ORDER BY software.name");
			DataTable diskTable = Database.ExecuteFill(Globals.Core.ConnectionStrings[1], "SELECT part.software_id, disk.name, disk.sha1 FROM (part INNER JOIN diskarea ON part.part_id = diskarea.part_id) INNER JOIN disk ON diskarea.diskarea_id = disk.diskarea_id WHERE (disk.sha1 IS NOT NULL)");

			// First pass: count total software with missing disks and collect them
			List<Tuple<DataRow, DataRow, int>> softwareToProcess = new List<Tuple<DataRow, DataRow, int>>();

			foreach (DataRow softwarelistRow in softwarelistTable.Rows)
			{
				string softwarelist_name = (string)softwarelistRow["name"];

				if (ignoreListNames.Contains(softwarelist_name) == true)
					continue;

				long softwarelist_id = (long)softwarelistRow["softwarelist_id"];
				
				foreach (DataRow softwareRow in softwareTable.Select($"softwarelist_id = {softwarelist_id}"))
				{
					int dontHaveCount = 0;
					long software_id = (long)softwareRow["software_id"];
					
					foreach (DataRow diskRow in diskTable.Select($"software_id = {software_id}"))
					{
						string sha1 = (string)diskRow["sha1"];
						if (Globals.DiskHashStore.Exists(sha1) == false)
							++dontHaveCount;
					}

					if (dontHaveCount > 0)
					{
						softwareToProcess.Add(new Tuple<DataRow, DataRow, int>(softwarelistRow, softwareRow, dontHaveCount));
					}
				}
			}

			int totalSoftware = softwareToProcess.Count;
			Console.WriteLine($"Found \u001b[96m{totalSoftware}\u001b[0m software items with missing disk files.");
			Console.WriteLine();

			// Second pass: process software
			int currentSoftware = 0;
			foreach (var item in softwareToProcess)
			{
				currentSoftware++;
				DataRow softwarelistRow = item.Item1;
				DataRow softwareRow = item.Item2;
				int dontHaveCount = item.Item3;

				string softwarelist_name = (string)softwarelistRow["name"];
				string software_name = (string)softwareRow["name"];

				Console.WriteLine($"\u001b[93m[{currentSoftware}/{totalSoftware}]\u001b[0m Processing software: \u001b[96m{softwarelist_name}/{software_name}\u001b[0m (\u001b[91m{dontHaveCount} missing disks\u001b[0m)");
				
				// Download and place
				Place.PlaceSoftwareDisks(Globals.Core, softwarelistRow, softwareRow, true);
			}

			Console.WriteLine($"\u001b[92mProcessed {currentSoftware} of {totalSoftware} software items with missing disks.\u001b[0m");
		}

		/// <summary>
		/// Places software ROM and/or disk assets for a specific machine and software combination.
		/// </summary>
		public static void PlaceSoftwareAssets(ICore core, string machineName, string softwareName, bool placeRoms, bool placeDisks)
		{
			DataRow machine = core.GetMachine(machineName);
			if (machine == null)
				throw new ApplicationException($"Machine not found: {machineName}");

			DataRow[] softwarelists = core.GetMachineSoftwareLists(machine);
			bool softwareFound = false;

			foreach (DataRow machineSoftwarelist in softwarelists)
			{
				string softwarelistName = (string)machineSoftwarelist["name"];
				DataRow softwarelist = core.GetSoftwareList(softwarelistName);

				if (softwarelist == null)
				{
					Console.WriteLine($"!!! DATA Error Machine's '{machineName}' software list '{softwarelistName}' missing.");
					continue;
				}

				foreach (DataRow findSoftware in core.GetSoftwareListsSoftware(softwarelist))
				{
					if ((string)findSoftware["name"] == softwareName)
					{
						if (placeRoms)
							Place.PlaceSoftwareRoms(core, softwarelist, findSoftware, true);
						
						if (placeDisks)
							Place.PlaceSoftwareDisks(core, softwarelist, findSoftware, true);

						softwareFound = true;
						break;
					}
				}

				if (softwareFound)
					break;
			}

			if (!softwareFound)
				throw new ApplicationException($"Software not found: {machineName}, {softwareName}");
		}

	}

	public enum PayloadLevel { Root, Subset, Datafile, Game, Machine, Softwarelist, Software };

	public class Counts
	{
		public long Datafiles = 0;
		public long Games = 0;
		public long Roms = 0;
		public long Size = 0;
		public Dictionary<string, int> Extentions = new Dictionary<string, int>();

		public void Add(Counts counts)
		{
			Datafiles += counts.Datafiles;
			Games += counts.Games;
			Roms += counts.Roms;
			Size += counts.Size;

			foreach (var extention in counts.Extentions)
			{
				if (Extentions.ContainsKey(extention.Key) == false)
					Extentions.Add(extention.Key, 0);
				Extentions[extention.Key] += extention.Value;
			}
		}

		public void AddExtention(string extention)
		{
			if (extention.Length == 0)
				extention = "_";
			else
				extention = extention.Substring(1);

			if (Extentions.ContainsKey(extention) == false)
				Extentions.Add(extention, 0);

			Extentions[extention] += 1;
		}

		public string ExtentionsToString()
		{
			int max = 10;

			var extentions = Extentions.OrderByDescending(pair => pair.Value).Cast<KeyValuePair<string, int>>();

			if (extentions.Count() > max)
			{
				int remainingCount = 0;
				foreach (int count in extentions.Skip(10).Select(pair => pair.Value))
					remainingCount += count;

				extentions = extentions.Take(max);
				extentions = extentions.Append(new KeyValuePair<string, int>("...", remainingCount));
				extentions = extentions.OrderByDescending(pair => pair.Value);
			}

			return String.Join(", ", extentions.Select(pair => $"{pair.Key}({pair.Value})"));
		}
	}

	public class PayloadLevelInfo
	{
		public DataTable DataTable;

		public Counts Counts = new Counts();

		private string HtmlTitle;
		private StringBuilder HtmlPage = new StringBuilder();

		private int TableWidth = 0;

		private Dictionary<string, string[]> XmlJsonPayloads;

		public PayloadLevelInfo(
			PayloadLevel level,
			Dictionary<string, string[]> xmlJsonPayloads)
		{
			XmlJsonPayloads = xmlJsonPayloads;

			switch (level)
			{
				case PayloadLevel.Root:
					DataTable = Operations.MakePayloadDataTable("root_payload", new string[] { "key_1" });
					break;

				case PayloadLevel.Subset:
					DataTable = Operations.MakePayloadDataTable("subset_payload", new string[] { "subset_name" });
					break;

				case PayloadLevel.Datafile:
					DataTable = Operations.MakePayloadDataTable("datafile_payload", new string[] { "subset_name", "datafile_name" });
					break;

				case PayloadLevel.Game:
					DataTable = Operations.MakePayloadDataTable("game_payload", new string[] { "subset_name", "datafile_name", "game_name" });
					break;

				case PayloadLevel.Machine:
					DataTable = Operations.MakePayloadDataTable("machine_payload", new string[] { "machine_name" });
					break;

				case PayloadLevel.Softwarelist:
					DataTable = Operations.MakePayloadDataTable("softwarelist_payload", new string[] { "softwarelist_name" });
					break;

				case PayloadLevel.Software:
					DataTable = Operations.MakePayloadDataTable("software_payload", new string[] { "softwarelist_name", "software_name" });
					break;

				default:
					throw new ApplicationException("On another level.");
			}
		}

		public void Start(string title)
		{
			if (HtmlPage.Length != 0)
				throw new ApplicationException("Unfinished Business");

			Counts = new Counts();

			HtmlTitle = title;
		}
		public void Finish(params string[] keys)
		{
			if (keys.Length != DataTable.PrimaryKey.Length)
				throw new ApplicationException("Bad keys width");

			if (DataTable.Rows.Find(keys) != null)
			{
				Console.WriteLine($"!!! Warning Duplicate Item {DataTable.TableName}:\t{String.Join("\t", keys)}");
			}
			else
			{
				HtmlPage.AppendLine("<br />");

				string[] xmlJson = new string[] { "", "" };
				if (XmlJsonPayloads != null)
				{
					string key = String.Join("\t", keys);

					if (XmlJsonPayloads.ContainsKey(key) == false)
						throw new ApplicationException($"Did not find xml json lookup {key}");
					xmlJson = XmlJsonPayloads[key];
				}

				var rowData = new List<object>();
				rowData.AddRange(keys);
				rowData.AddRange(new string[] { HtmlTitle, xmlJson[0], xmlJson[1], HtmlPage.ToString() });

				DataTable.Rows.Add(rowData.ToArray());
			}

			HtmlPage.Length = 0;
		}

		public void Append(string html)
		{
			HtmlPage.AppendLine(html);
		}
		public void Append(DataRow row)
		{
			Append(new DataRow[] { row });
		}
		public void Append(IEnumerable<DataRow> rows)
		{
			if (rows.Any() == false)
				return;

			string[] columnNames = rows.First().Table.Columns.Cast<DataColumn>().Select(col => col.ColumnName).Where(name => name.EndsWith("_id") == false).ToArray();

			TableStart(columnNames);
			foreach (var row in rows)
				TableRow(columnNames.Select(col => row.IsNull(col) ? "" : (string)row[col]).ToArray());
			TableEnd();
		}
		public void TableStart(params string[] columnNames)
		{
			TableWidth = columnNames.Length;
			HtmlPage.AppendLine("<table>");
			HtmlPage.AppendLine(EncodeTableRow(columnNames, "th"));
		}
		public void TableRow(params string[] values)
		{
			if (values.Length != TableWidth)
				throw new ApplicationException("Bad values width");

			HtmlPage.AppendLine(EncodeTableRow(values, "td"));
		}

		public void TableEnd()
		{
			HtmlPage.AppendLine("</table>");
		}

		private string EncodeTableRow(IEnumerable<string> values, string type)
		{
			values = values.Select(value => {
				if (value != null && value.StartsWith("<a href") == false)
					value = WebUtility.HtmlEncode(value);
				return value;
			});

			return $"<tr>{String.Join("", values.Select(value => $"<{type}>{value}</{type}>"))}</tr>";
		}

		public void Save(SqlConnection connection)
		{
			Operations.MakeMSSQLPayloadsInsert(connection, DataTable);
		}
	}
}
