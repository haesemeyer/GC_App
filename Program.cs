using System;
using System.IO;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

using MHApi.Threading;
using MHApi.DrewsClasses.Temperature;
using MHApi.DrewsClasses;

using Pastel;

namespace GC_App
{
    class Program
    {
        //gradient chamber design
        const int NUM_PELTIERS = 3;
        const int NUM_THERMISTORS = 6;
        const int MIN_TEMP = 15;
        const int MAX_TEMP = 40;
        const double KI = 0.005;
        const double KP = 0.01;
        const double VEL = 2.0;
        //console display
        const int CONSOLE_WIDTH = 60;
        const int CONSOLE_HEIGHT = 27;
        const int INPUT_TEMP_ROW = 22;
        const int TEMP_DISP_ROW = 3;
        static readonly int[] TEMP_COLS = { 11, 27, 45 };
        //general variables
        static object _printLock = new object();  //lock object used to set cursor
        static string _save_file;  //location to save file
        static int _peltier_config;
        static double[] _temps = new double[NUM_PELTIERS];
        static readonly string[] _schematic = File.ReadAllLines(@"C:\Users\GradientChamber\source\repos\GC_App\temp_display.txt");

        //set up daq board
        static PCIe6323 _daq = new PCIe6323("dev1", PCIe6323.AIReadTriggerType.None);
        //create thermistors
        static Thermistor[] _thermistors =
        {
            new Thermistor(_daq, "ai0", ThermistorType.test),
            new Thermistor(_daq, "ai1", ThermistorType.test),
            new Thermistor(_daq, "ai2", ThermistorType.LFpolyimide),
            new Thermistor(_daq, "ai3", ThermistorType.test),
            new Thermistor(_daq, "ai4", ThermistorType.test),
            new Thermistor(_daq, "ai5", ThermistorType.USsensor)
        };
        //create peltier
        static Peltier[] _peltier =
        {
            new Peltier("dev1", "ctr0", "PFI12", 100, "Port0/line0"),
            new Peltier("dev1", "ctr1", "PFI13", 100, "Port1/line0"),
            new Peltier("dev1", "ctr2", "PFI14", 100, "Port1/line1"),
        };
        //create temperature controllers
        static TemperatureController[] _tempControl =
        {
            new TemperatureController(_thermistors[0], _peltier[0], 0, 0, VEL, "TController1"),
            new TemperatureController(_thermistors[2], _peltier[1], 0, 0, VEL, "TController2"),
            new TemperatureController(_thermistors[4], _peltier[2], 0, 0, VEL, "TController3")
        };
        static bool _controlRunning = false;

        //create reset events
        static AutoResetEvent _tempGet = new AutoResetEvent(false);
        static AutoResetEvent _tempGot = new AutoResetEvent(false);
        static ManualResetEvent _exitEvent = new ManualResetEvent(false);

        //create threads
        static WorkerT<Object> _IOworker = new WorkerT<Object>(IOloop, null, false);
        static WorkerT<int> _TWworker = new WorkerT<int>(TWloop, 100, false);

        //record current time
        static DateTime _time = DateTime.Now;

        static void Main(string[] args)
        {
            // Prepare console
            Console.Clear();
            Console.SetWindowSize(CONSOLE_WIDTH, CONSOLE_HEIGHT);
            Console.SetBufferSize(CONSOLE_WIDTH, CONSOLE_HEIGHT);

            // Obtain session information for save file
            string[] info = File.ReadAllLines(@"C:\Users\GradientChamber\source\repos\GC_App\session-info.txt");
            string folderName = @"C:\Users\GradientChamber\Desktop\GCtemps\";
            var regex = new Regex(".*(Desktop.*)$");  //string matching to print truncated version of file path
           
            // File variables
            string experimenter = null; 
            string session_id = null;
            string timestamp = _time.ToString("yyMMdd_HH_mm");

            // Loop variables
            ConsoleKeyInfo cki1; 
            bool infopass = false;
            // User input loop for file path information
            lock (_printLock)
            {
                while (!infopass)
                {
                    //print splash screen, and obtain user info
                    Console.SetCursorPosition(0, 0);
                    PrintFile(info);
                    ClearLine(20); Console.SetCursorPosition(0, 20);
                    Console.Write($"\t\t{"Timestamp".Pastel(Color.MediumVioletRed)}: " + timestamp);
                    //user name input
                    ClearLine(21); Console.SetCursorPosition(0, 21);
                    Console.Write($"\t\t{"Experimenter".Pastel(Color.MediumVioletRed)}: ");
                    experimenter = Console.ReadLine();
                    //session id input
                    ClearLine(22); Console.SetCursorPosition(0, 22);
                    Console.Write($"\t\t{"Session ID".Pastel(Color.MediumVioletRed)}: ");
                    session_id = Console.ReadLine();

                    //create experimeter directory if it does not exist
                    string pathString = Path.Combine(folderName, Regex.Replace(experimenter.ToLower(), @"\s+", ""));
                    if (!Directory.Exists(pathString))
                    {
                        try
                        {
                            Directory.CreateDirectory(pathString);
                            _save_file = Path.Combine(pathString,  timestamp + "_" + session_id + ".txt");
                            infopass = true;
                        }
                        catch
                        {
                            ClearLine(23); ClearLine(24); Console.SetCursorPosition(0, 23);
                            Console.WriteLine($"\t{"Unable to create experimenter directory".Pastel(Color.DeepSkyBlue)}\n");
                            infopass = false;
                        }
                    }
                    else
                    {
                        _save_file = Path.Combine(pathString,  timestamp + "_" + session_id + ".txt");
                        infopass = true;
                    }

                    //redo if the directory could not be made, or if the user wishes to
                    if (infopass && IsValidPath(_save_file))
                    {
                        ClearLine(23); ClearLine(24); Console.SetCursorPosition(0, 23);
                        Console.WriteLine($"\t\t{"Save file location".Pastel(Color.MediumVioletRed)}:\n\t" +
                                            regex.Match(_save_file).Groups[1].Value);
                        Console.Write("\n\tPress R to redo, any other key continues");
                        cki1 = Console.ReadKey(true);
                        if (cki1.Key == ConsoleKey.R || cki1.Key == ConsoleKey.Escape)
                        {
                            ClearLine(23); ClearLine(24); ClearLine(26);
                            infopass = false;
                        }
                    }
                    else
                    {
                        ClearLine(23); ClearLine(24); Console.SetCursorPosition(0, 23);
                        Console.WriteLine($"\t{"File path invalid".Pastel(Color.DeepSkyBlue)}\n");
                        infopass = false;
                    }
                }
                Console.Clear(); Console.SetCursorPosition(0, 0);
            }

            // Begin new file and write to it            
            _ = WriteAsync(String.Format("Experimenter: {0}", experimenter));
            _ = WriteAsync(String.Format("Session ID: {0}", session_id));
            _ = WriteAsync(String.Format("Start time: {0}", _time.ToString("MM/dd/yyyy hh:mm tt")));
            _ = WriteAsync("\nTime(s)\tAI0\tAI1\tAI2\tAI3\tAI4\tAI5");

            // Obtain peltier configuration
            string[] config = File.ReadAllLines(@"C:\Users\GradientChamber\source\repos\GC_App\peltier_config.txt");
			lock (_printLock)
			{
				PrintFile(config);
                do
                {
                    ClearLine(24); Console.SetCursorPosition(0, 24);
                    Console.Write($"\t\t{"Enter peltier configuration".Pastel(Color.MediumVioletRed)}: ");
                } while (!int.TryParse(Console.ReadLine(), out _peltier_config) || 
                         Math.Abs(_peltier_config) > 4 || _peltier_config == 0);
                Console.Clear(); Console.SetCursorPosition(0, 0);
			}

            // Initialize system
            _temps = GetTemps();                // (1) get initial temperatures from user
            _daq.AI.Start(2500, 100);           // (2) Begin AI
            _daq.AI.ReadThreadReady.WaitOne();  // (3) Wait for AI system
            _IOworker.Start();          // (4) begin (DAQ) printing values from thermistors
            _TWworker.Start();          // (5) begin writing values from thermistors
            StartTemperatureControl();  // (6) begin control loop

            // User input and console update loop
            ConsoleKeyInfo cki;
            while (!_exitEvent.WaitOne(100))
            {
                cki = Console.ReadKey(true);
                // Execute depending on key press
                switch (cki.Key)
                {
                    case ConsoleKey.E:
                        _TWworker.Stop();
                        _IOworker.Stop();
                        _exitEvent.Set();
                        break;
                    case ConsoleKey.T:
                        _tempGet.Set(); //holds DAQ printing
                        _temps = GetTemps();
                        for (int i = 0; i < NUM_PELTIERS; i++)
                            UpdateTemperature(_tempControl[i], _temps[i]);
                        _tempGot.Set(); //releases DAQ hold
                        break;
                    default:
                        continue;
                }
            }
            // Handle disposing
            StopTemperatureControl(); //handles temperature controllers and peltiers
            _daq.Dispose();
            _IOworker.Dispose();
            _TWworker.Dispose();

            // Keep the console open in debug mode.
            lock (_printLock)
            {
                Console.SetCursorPosition(0, 25);
                Console.Write($"\t\t   {"Press any key to exit".Pastel(Color.AliceBlue)}.");
                Console.ReadKey();
            }
        }

        #region Console loop
        static void IOloop(AutoResetEvent stop, Object data)
        {
            while (!stop.WaitOne(100))
            {
                lock (_printLock)
                {
                    // Keyboard instructions
                    Console.SetCursorPosition(0, 0);
                    Console.WriteLine("\t[T]: to update temperature, [E]: to end program");
                    ClearLine(1);

                    // Temperature display
                    for (int i = 0; i < NUM_THERMISTORS; i++)
                    {
                        if (i < 3)
                            PrintTemp(TEMP_COLS[i], TEMP_DISP_ROW - 1, _thermistors[i].Temperature.ToString("0.000") + "°C ");
                        else
                            PrintTemp(TEMP_COLS[i - 3], TEMP_DISP_ROW, _thermistors[i].Temperature.ToString("0.000") + "°C ");
                    }
                }

                // Halt printing if user updates temperatures
                if (_tempGet.WaitOne(100)) _tempGot.WaitOne();
            }
        }
		#endregion Console loop

		#region Text writer
        static void TWloop(AutoResetEvent stop, int period)
		{
            Thread.Sleep(period); // wait a period for DAQ
            while (!stop.WaitOne(period))
            {
                // Print temperatures to file
                _ = WriteAsync(String.Format("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}",
                                TimeSpan.FromTicks(DateTime.Now.Ticks - _time.Ticks).TotalSeconds.ToString("0.000"),
                                _thermistors[0].Temperature.ToString("0.000"), _thermistors[1].Temperature.ToString("0.000"),
                                _thermistors[2].Temperature.ToString("0.000"), _thermistors[3].Temperature.ToString("0.000"),
                                _thermistors[4].Temperature.ToString("0.000"), _thermistors[5].Temperature.ToString("0.000")));
            }
            
        }

        #endregion Text writer

        #region Temperature controlling
        /// <summary>
        /// Starts the AI reading service and hence temperature control
        /// </summary>
        static public void StartTemperatureControl()
        {
            //set target temperatures
            _tempControl[0].TargetTemperature = _temps[0];
            _tempControl[1].TargetTemperature = _temps[1];
            _tempControl[2].TargetTemperature = _temps[2];
            //set control parameters
            _tempControl[0].KI = _tempControl[1].KI = _tempControl[2].KI = KI;
            _tempControl[0].KP = _tempControl[1].KP = _tempControl[2].KP = KP;
            _tempControl[0].Velocity = _tempControl[1].Velocity = _tempControl[2].Velocity = VEL;
            //start temperature controllers
            foreach (TemperatureController tc in _tempControl)
                tc.Start();
            _controlRunning = true;
        }

		/// <summary>
		/// Stops the AI reading service and hence temperature control
		/// </summary>
		static public void StopTemperatureControl()
        {
            if (!_controlRunning)
                return;
            //stop temperature controllers
            _tempControl[0].Stop();
            _tempControl[1].Stop();
            _tempControl[2].Stop();
            //Dispose temperature controllers and peltiers
            _tempControl[0].Dispose();
            _tempControl[1].Dispose();
            _tempControl[2].Dispose();
            _peltier[0].Dispose();
            _peltier[1].Dispose();
            _peltier[2].Dispose();
            //stop daq
            _daq.AI.Stop();
            _controlRunning = false;
        }

        /// <summary>
        /// Updates the set value of all 4 temperature controllers
        /// </summary>
        /// <param name="newTemp">The new desired set temperature</param>
        /// <param name="tc">Temporary variable for the temperature controller</param>
        static public void UpdateTemperature(TemperatureController tc, double newTemp)
        {
            if (newTemp != tc.TargetTemperature) //only bother temperature controllers if temperature actually different
            {
                //The following is kinda stupid, maybe needs reworking - done because cooling takes VERY LONG
                //so a large error accumulates in the controllers
                if (newTemp > tc.TargetTemperature)
                {
                    tc.ResetIntegralOfError();
                }
                //Update temperature controllers
                tc.TargetTemperature = newTemp;
            }
        }
        #endregion Temperature controlling

        #region Get temperatures from user
        static double[] GetTemps()
		{
            double[] temps = new double[NUM_PELTIERS];
            lock (_printLock)
            {
                // Clear consoles lines
                ClearLine(0); ClearLine(INPUT_TEMP_ROW);
                // Display schematic
                PrintFile(_schematic);                
                // Accept new temps
                switch (_peltier_config)
                {
                    case 1:
                        Console.SetCursorPosition(TEMP_COLS[1]+1, INPUT_TEMP_ROW); 
                        Console.Write('X');
                        temps[0] = ReadTemp(TEMP_COLS[0], INPUT_TEMP_ROW);
                        temps[2] = ReadTemp(TEMP_COLS[2], INPUT_TEMP_ROW);
                        break;
                    case 2:
                        temps[0] = ReadTemp(TEMP_COLS[0], INPUT_TEMP_ROW);
                        temps[2] = ReadTemp(TEMP_COLS[2], INPUT_TEMP_ROW);
                        temps[1] = (temps[2] + temps[0]) / 2;
                        PrintTemp(TEMP_COLS[1], INPUT_TEMP_ROW, "*" + temps[1].ToString("0.000") + "*");
                        break;
                    default:
                        for (int i = 0; i < 3; i++)
                            temps[i] = ReadTemp(TEMP_COLS[i], INPUT_TEMP_ROW);
                        break;
                }
            }
            return temps;
        }
		#endregion Get temperatures from user

		#region Print temperatures to console
        static void PrintTemp(int col, int row, string str_temp)
		{
            Console.SetCursorPosition(col-2, row);
            Console.Write("         \b\b\b\b\b\b\b\b\b"); //clears text
            Console.Write(str_temp); 
        }
		#endregion Print temperatures to console

		#region Temperature input user-proofing 
        static double ReadTemp(int col, int row)
		{
            double temp;
            Console.SetCursorPosition(col, row);
            while(!double.TryParse(Console.ReadLine(), out temp) || temp < MIN_TEMP || temp > MAX_TEMP)
            {
                Console.SetCursorPosition(col, row);
                Console.Write("      \b\b\b\b\b\b");
            }
            return temp;
        }
        #endregion Temperature input user-proofing

        #region Print file
        static void PrintFile(string[] lines)
        {
            Console.SetCursorPosition(0, 0);
            foreach (string line in lines)
            {
                // Use a tab to indent each line of the file.
                Console.WriteLine("\t" + line);
            }
        }
        #endregion Print file

        #region Clear console line
        static void ClearLine(int row)
        {
            Console.SetCursorPosition(0, row);
            Console.Write(new String(' ', Console.BufferWidth));
        }
        #endregion Clear console line

        #region Write to file
        public static async Task WriteAsync(string Line)
        {
            using (StreamWriter file = new StreamWriter(_save_file, append: true))
                await file.WriteLineAsync(Line);
        }
        #endregion Write to file

        #region Is file path valid
        static bool IsValidPath(string path, bool allowRelativePaths = false)
        {
            bool isValid;
            try
            {
                string fullPath = Path.GetFullPath(path);

                if (allowRelativePaths)
                {
                    isValid = Path.IsPathRooted(path);
                }
                else
                {
                    string root = Path.GetPathRoot(path);
                    isValid = String.IsNullOrEmpty(root.Trim(new char[] { '\\', '/' })) == false;
                }
            }
            catch
            {
                isValid = false;
            }

            return isValid;
        }
		#endregion Is file path valid
	}
}
