using System;
using System.Linq;
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
        const int MIN_TEMP = 18;
        const int MAX_TEMP = 54;
        const int MAIN_LOOP_FREQ = 1 / 10 * 1000; // (1/Freq)*unit conversion [=] millisecond
        //console display
        const int CONSOLE_WIDTH = 60;
        const int CONSOLE_HEIGHT = 28;
        const int TIMESTAMP_ROW = CONSOLE_HEIGHT - 1;
        const int INPUT_TEMP_ROW = 22;
        const int TEMP_DISP_ROW = 3;
        static readonly int[] TEMP_COLS = { 11, 28, 45 };
        //general variables
        static object _printLock = new object();  //lock object used to set cursor
        static string _pathString; //location to save file directory
        static string _saveFile;  //location to save file
        static int _peltier_config;
        static double[] _temps = new double[NUM_PELTIERS];
        //file locations and text
        static readonly string[] _schematic = File.ReadAllLines(@"C:\Users\GradientChamber\source\repos\GC_App\temp_display.txt");
        static readonly string[] _config = File.ReadAllLines(@"C:\Users\GradientChamber\source\repos\GC_App\peltier_config.txt");
        static readonly string[] _info = File.ReadAllLines(@"C:\Users\GradientChamber\source\repos\GC_App\session-info.txt");
        //controller properties with defaults
        static double _kP = 0.1;
        static double _kI = 0.0002;
        static double _kD = 0.05;
        static double _vel = 0.025;
        static bool _controlRunning = false;

        //set up daq board
        static PCIe6323 _daq = new PCIe6323("dev1", PCIe6323.AIReadTriggerType.None);

        //create thermistors
        static Thermistor[] _thermistors =
        {
            new Thermistor(_daq, "ai0", ThermistorType.LFpolyimide),
            new Thermistor(_daq, "ai1", ThermistorType.LFpolyimide),
            new Thermistor(_daq, "ai2", ThermistorType.LFpolyimide),
            new Thermistor(_daq, "ai3", ThermistorType.LFpolyimide),
            new Thermistor(_daq, "ai4", ThermistorType.LFpolyimide),
            new Thermistor(_daq, "ai5", ThermistorType.LFpolyimide)
        };
        //create peltier
        static Peltier[] _peltier =
        {
            new Peltier("dev1", "ctr0", "PFI12", 1000, "Port0/line6"),
            new Peltier("dev1", "ctr1", "PFI13", 1000, "Port1/line0"),
            new Peltier("dev1", "ctr2", "PFI14", 1000, "Port0/line5"),
        };
        //create temperature controllers and parameters
        static TemperatureController[] _tempControl =

        {
            new TemperatureController(_thermistors[1], _peltier[0], _kP, _kI, _kD, _vel, "Back_Controller"),
            new TemperatureController(_thermistors[3], _peltier[1], _kP, _kI, _kD, _vel, "Mid_Controller"),
            new TemperatureController(_thermistors[5], _peltier[2], _kP, _kI, _kD, _vel, "Front_Controller")
        };

        //create reset events
        static AutoResetEvent _tempGet = new AutoResetEvent(false);
        static AutoResetEvent _tempGot = new AutoResetEvent(false);
        static ManualResetEvent _exitEvent = new ManualResetEvent(false);

        //create threads
        static WorkerT<Object> _IOworker = new WorkerT<Object>(IOloop, null, false);
        static WorkerT<int> _TWworker;

        //record current time
        static DateTime _time;
		//timestamping variables
		static int _ts = 0;
        static DateTime _ts_time;

        static void Main(string[] args)
        {
            // Prepare console
            Console.Clear();
            Console.SetWindowSize(CONSOLE_WIDTH, CONSOLE_HEIGHT);
            Console.SetBufferSize(CONSOLE_WIDTH, CONSOLE_HEIGHT);

            // Obtain session information for save file
            // File variables
            string experimenter = null;
            string session_id = null;
            string origin_timestamp = DateTime.Now.ToString("yyMMdd_HH_mm");
            string folderName = @"C:\Users\GradientChamber\Desktop\GCtemps\";
            var regex = new Regex(".*(Desktop.*)$");  //string matching to print truncated version of file pat

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
                    PrintFile(_info);
                    ClearLine(20); Console.SetCursorPosition(0, 20);
                    Console.Write($"\t\t{"Timestamp".Pastel(Color.MediumVioletRed)}: " + origin_timestamp);
                    //user name input
                    do
                    {
                        ClearLine(21); Console.SetCursorPosition(0, 21);
                        Console.Write($"\t\t{"Experimenter".Pastel(Color.MediumVioletRed)}: ");
                        experimenter = Console.ReadLine();
                    } while (String.IsNullOrEmpty(experimenter));

                    //session id input
                    do
                    {
                        ClearLine(22); Console.SetCursorPosition(0, 22);
                        Console.Write($"\t\t{"Session ID".Pastel(Color.MediumVioletRed)}: ");
                        session_id = Console.ReadLine();
                    } while (String.IsNullOrEmpty(session_id));

                    //create experimeter directory if it does not exist
                    _pathString = Path.Combine(folderName, Regex.Replace(experimenter.ToLower(), @"\s+", ""));
                    _saveFile = Path.Combine(_pathString, origin_timestamp + "_" + session_id + ".txt");
                    if (!Directory.Exists(_pathString))
                    {
                        try
                        {
                            Directory.CreateDirectory(_pathString);
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
                        infopass = true;
                    }

                    //redo if the directory could not be made, or if the user wishes to
                    if (infopass)
                    {
                        if (IsValidPath(_saveFile))
                        {
                            ClearLine(23); ClearLine(24); Console.SetCursorPosition(0, 23);
                            Console.WriteLine($"\t\t{"Save file location".Pastel(Color.MediumVioletRed)}:\n\t" +
                                                regex.Match(_saveFile).Groups[1].Value);
                            Console.Write($"\n\t{"Press R to redo, any other key continues".Pastel(Color.DeepSkyBlue)}");
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
                            Console.WriteLine($"\t\t{"File name invalid".Pastel(Color.DeepSkyBlue)}\n");
                            infopass = false;
                        }
                    }
                }
                Console.Clear(); Console.SetCursorPosition(0, 0);
            }

            // Obtain peltier configuration and create recording thread,
            // recording thermal data at different rate if calibrating
            lock (_printLock)
            {
                PrintFile(_config);
                do
                {
                    ClearLine(23); Console.SetCursorPosition(0, 23);
                    Console.Write($"\t\t{"Enter peltier configuration".Pastel(Color.MediumVioletRed)}: ");
                } while (!int.TryParse(Console.ReadLine(), out _peltier_config) ||
                        _peltier_config > 4 || _peltier_config < 1);

                // If calibrating, obtain new PID values from user
                if (_peltier_config == 4)
                {
                    // Obtain new values for PID controller
                    do
                    {
                        ClearLine(24); Console.SetCursorPosition(0, 24);
                        Console.Write($"\t\t{"Proprotional, P: ".Pastel(Color.SeaGreen)}: ");
                    } while (!double.TryParse(Console.ReadLine(), out _kP) || _kP < 0);

                    do
                    {
                        ClearLine(25); Console.SetCursorPosition(0, 25);
                        Console.Write($"\t\t{"Integral, I: ".Pastel(Color.SeaGreen)}: ");
                    } while (!double.TryParse(Console.ReadLine(), out _kI) || _kI < 0);

                    do
                    {
                        ClearLine(26); Console.SetCursorPosition(0, 26);
                        Console.Write($"\t\t{"Derivative, D: ".Pastel(Color.SeaGreen)}: ");
                    } while (!double.TryParse(Console.ReadLine(), out _kD) || _kD < 0);

                    do
                    {
                        ClearLine(27); Console.SetCursorPosition(0, 27);
                        Console.Write($"\t\t{"Velocity, vel: ".Pastel(Color.SeaGreen)}: ");
                    } while (!double.TryParse(Console.ReadLine(), out _vel) || _vel < 0);

                    // Initialize text writer loop at 10Hz
                    _TWworker = new WorkerT<int>(TWloop, 100, false); //set record rate
                }
                else // Otherwise load last saved by user
                {
                    // Load last saved PID controller
                    double[] ctrls = PIDcontrols(_pathString);
                    _kP = ctrls[0];
                    _kI = ctrls[1];
                    _kD = ctrls[2];
                    _vel = ctrls[3];

                    // Initialize text writer loop at 1Hz
                    _TWworker = new WorkerT<int>(TWloop, 1000, false); //set record rate
                }
                Console.Clear(); Console.SetCursorPosition(0, 0);
            }

            // Initialize system -- Part I
            _temps = GetTemps();                // (1) get initial temperatures from user
            _daq.AI.Start(2500, 100);           // (2) Begin AI
            _daq.AI.ReadThreadReady.WaitOne();  // (3) Wait for AI system
            _IOworker.Start();                  // (4) begin (DAQ) printing values from thermistors

            // Initialize system -- Part II -- set times and begin writing to file            
            _time = _ts_time = DateTime.Now;       
            _ = WriteAsync(_saveFile, String.Format("Experimenter: {0}", experimenter));
            _ = WriteAsync(_saveFile, String.Format("Session ID: {0}", session_id));
            _ = WriteAsync(_saveFile, String.Format("Start time: {0}", _time.ToString("MM/dd/yyyy hh:mm tt")));
            _ = WriteAsync(_saveFile, String.Format("PIDv: {0}, {1}, {2}, {3}",
                           _kP.ToString(), _kI.ToString(), _kD.ToString(), _vel.ToString()));
            _ = WriteAsync(_saveFile, "\nTime(s)\tAI0\tAI1\tAI2\tAI3\tAI4\tAI5\tD_AI01\tD_AI23\tD_AI45");
            _TWworker.Start();                   // (5) begin writing values from thermistors

            // Initialize system -- Part III
            StartTemperatureControl();          // (6) begin control loop

            // User input and console update loop
            Console.CursorVisible = false;
            ConsoleKeyInfo cki;
            while (!_exitEvent.WaitOne(MAIN_LOOP_FREQ))
            {
                cki = Console.ReadKey(true);
                // Execute depending on key press
                switch (cki.Key)
                {
                    case ConsoleKey.E:
                        _TWworker.Stop();
                        _IOworker.Stop();
                        _exitEvent.Set();
                        ClearLine(0);
                        break;
                    case ConsoleKey.U:
                        _tempGet.Set(); //holds DAQ printing
                        _temps = GetTemps();
                        for (int i = 0; i < NUM_PELTIERS; i++)
                            UpdateTemperature(_tempControl[i], _temps[i]);
                        _tempGot.Set(); //releases DAQ hold
                        break;
                    case ConsoleKey.S:
                        _ts += 1;
                        _ts_time = DateTime.Now;
                        _ = WriteAsync(_saveFile, String.Format("Timestamp #{0} + {1}",
                            _ts, TimeSpan.FromTicks(_ts_time.Ticks - _time.Ticks).TotalSeconds.ToString("0.000")));
                        break;
                    case ConsoleKey.P:
                        if (_tempControl[0].KP == 0)
                            _tempControl[0].KP = _tempControl[1].KP = _tempControl[2].KP = 3;
                            //_tempControl[0].KI = _tempControl[1].KI = _tempControl[2].KI = 0.77;
                            //_tempControl[0].KD = _tempControl[1].KD = _tempControl[2].KD = 28.6;
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

        #region Temp & time to console loop
        static void IOloop(AutoResetEvent stop, Object data)
        {
            while (!stop.WaitOne(100))
            {
                lock (_printLock)
                {
                    // Temperature display
                    for (int i = 0; i < NUM_THERMISTORS; i++)
                    {
                        if (i % 2 == 0)
                            PrintTemp(TEMP_COLS[i / 2], TEMP_DISP_ROW - 1, _thermistors[i].Temperature.ToString("0.000") + "°C ");
                        else
                            PrintTemp(TEMP_COLS[(i - 1) / 2], TEMP_DISP_ROW, _thermistors[i].Temperature.ToString("0.000") + "°C ");
                    }

                    // Time display
                    ClearLine(TIMESTAMP_ROW);
                    Console.Write($"{"Time since stamp #{0}:".Pastel(Color.MediumVioletRed)}", _ts);
                    Console.SetCursorPosition(CONSOLE_WIDTH - 9, TIMESTAMP_ROW);
                    Console.Write($"{"{0}".Pastel(Color.MediumVioletRed)}", TimeSpan.FromTicks(DateTime.Now.Ticks - _ts_time.Ticks).ToString(@"hh\:mm\:ss"));
                }

                // Halt printing if user updates temperatures
                if (_tempGet.WaitOne(100))  _tempGot.WaitOne();
            }
        }
        #endregion Temp & time to console loop

        #region Temp to file loop
        static void TWloop(AutoResetEvent stop, int period)
        {
            //Thread.Sleep(period); // useful to allow NI cards & software to complete initialization
            while (!stop.WaitOne(period))
            {
                // Print temperatures to file
                _ = WriteAsync(_saveFile, String.Format("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8}\t{9}",
                                TimeSpan.FromTicks(DateTime.Now.Ticks - _time.Ticks).TotalSeconds.ToString("0.000"),
                                _thermistors[0].Temperature.ToString("0.000"), _thermistors[1].Temperature.ToString("0.000"),
                                _thermistors[2].Temperature.ToString("0.000"), _thermistors[3].Temperature.ToString("0.000"),
                                _thermistors[4].Temperature.ToString("0.000"), _thermistors[5].Temperature.ToString("0.000"),
                                (_peltier[2].DutyCycle * (double)_peltier[2].PeltierMode).ToString("0.000"),
                                (_peltier[1].DutyCycle * (double)_peltier[1].PeltierMode).ToString("0.000"),
                                (_peltier[0].DutyCycle * (double)_peltier[0].PeltierMode).ToString("0.000")));
            }
        }

        #endregion Temp to file loop

        #region Write temp to file
        public static async Task WriteAsync(string save_file, string Line)
        {
            using (StreamWriter file = new StreamWriter(save_file, append: true))
                await file.WriteLineAsync(Line);
        }
        #endregion Write temp to file

        #region Temperature controlling
        static public void StartTemperatureControl()
        {
            //set target temperatures
            _tempControl[0].TargetTemperature = _temps[0];
            _tempControl[1].TargetTemperature = _temps[1];
            _tempControl[2].TargetTemperature = _temps[2];
            //set control parameters
            _tempControl[0].KI = _tempControl[1].KI = _tempControl[2].KI = _kI;
            _tempControl[0].KP = _tempControl[1].KP = _tempControl[2].KP = _kP;
            _tempControl[0].KD = _tempControl[1].KD = _tempControl[2].KD = _kD;
            _tempControl[0].Velocity = _tempControl[1].Velocity = _tempControl[2].Velocity = _vel;
            //start temperature controllers
            _tempControl[0].Start(); _tempControl[2].Start();
            if (_peltier_config != 1) _tempControl[1].Start();
            _controlRunning = true;
        }

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
                ClearLine(0); ClearLine(INPUT_TEMP_ROW); Console.CursorVisible = true;
                // Display schematic
                PrintFile(_schematic);
                // Accept new temps
                switch (_peltier_config)
                {
                    case 1:
                        Console.SetCursorPosition(TEMP_COLS[1] + 1, INPUT_TEMP_ROW);
                        Console.Write('X');
                        temps[0] = ReadTemp(TEMP_COLS[0], INPUT_TEMP_ROW);
                        temps[2] = ReadTemp(TEMP_COLS[2], INPUT_TEMP_ROW);
                        break;
                    case 2:
                        temps[0] = ReadTemp(TEMP_COLS[0], INPUT_TEMP_ROW);
                        temps[2] = ReadTemp(TEMP_COLS[2], INPUT_TEMP_ROW);
                        temps[1] = (temps[2] + temps[0]) / 2;
                        PrintTemp(TEMP_COLS[1], INPUT_TEMP_ROW, "*" + temps[1].ToString("0.00") + "*");
                        break;
                    default:
                        for (int i = 0; i < 3; i++)
                            temps[i] = ReadTemp(TEMP_COLS[i], INPUT_TEMP_ROW);
                        break;
                }
                Console.CursorVisible = false;
                
                // Display keyboard instructions
                ClearLine(0);
                Console.Write("     [U]: update temps,  [S]: stamp time, [E]: end program");
            }
            return temps;
        }
        #endregion Get temperatures from user

        #region Temperature input user-proofing 
        static double ReadTemp(int col, int row)
        {
            double temp;
            Console.SetCursorPosition(col, row);
            while (!double.TryParse(Console.ReadLine(), out temp) || temp < MIN_TEMP || temp > MAX_TEMP)
            {
                Console.SetCursorPosition(col, row);
                Console.Write("      \b\b\b\b\b\b");
            }
            return temp;
        }
        #endregion Temperature input user-proofing

        #region Print temperatures to console
        static void PrintTemp(int col, int row, string str_temp)
        {
            Console.SetCursorPosition(col - 2, row);
            Console.Write("         \b\b\b\b\b\b\b\b\b"); //clears text
            Console.Write(str_temp);
        }
        #endregion Print temperatures to console

        #region Print file to console
        static void PrintFile(string[] lines)
        {
            Console.SetCursorPosition(0, 0);
            foreach (string line in lines)
            {
                // Use a tab to indent each line of the file.
                if (line == "| |  :               :                 :  | |")
                    Console.WriteLine($"\t| |{"  :               :                 :  ".PastelBg(Color.DodgerBlue)}| | ");
                //else if (line == "| ---:---------------:-----------------:--- |")
                //    Console.WriteLine($"\t| -{"--:---------------:-----------------:--".PastelBg(Color.DodgerBlue)}- | ");
                else
                    Console.WriteLine("\t" + line);
            }
        }
        #endregion Print file to console

        #region PID controller values from file
        public static double[] PIDcontrols(string directory)    
        {
            //locate latest file in experimenter directory
            var dirInfo = new DirectoryInfo(directory);
            var latestFile = dirInfo.GetFiles("*.txt")
                            .OrderByDescending(f => f.LastWriteTime)
                            .FirstOrDefault();
            double[] ctrls = new double[4];

            //search for "PIDv:" line in file and exit
            if (latestFile != null)
            {
                try
                {
                    foreach (string line in File.ReadLines(latestFile.FullName).Take(10))
                    {
                        if (line.IndexOf("PIDv", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            //find either 0.00 or 0E0 numbers, only
                            MatchCollection controls = Regex.Matches(line, @"\d+(\.\d+)?(e-\d+)?", RegexOptions.IgnoreCase);
							if (controls.Count < 4)
								throw new ArgumentException();

                            //convert match collection to double array
							for (int i = 0; i < controls.Count; i++)
							{
                                ctrls[i] = double.Parse(controls[i].Value);
							}
							return ctrls;
						}
					}
                    ctrls = new double[] { _kP, _kI, _kD, _vel }; //defaults
                    return ctrls;
                }
                catch
                {
                    ctrls = new double[] { _kP, _kI, _kD, _vel }; //defaults
                    return ctrls;
                }
            }
            else
            {
                ctrls = new double[] { _kP, _kI, _kD, _vel }; //defaults
                return ctrls;
            }

        }
        #endregion PID controller values from file

        #region Clear console line
        static void ClearLine(int row)
        {
            Console.SetCursorPosition(0, row);
            Console.Write(new String(' ', Console.BufferWidth));
            Console.SetCursorPosition(0, row);
        }
        #endregion Clear console line

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
