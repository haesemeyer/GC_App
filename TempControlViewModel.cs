using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Input;

using MHApi.GUI;
using MHApi.DrewsClasses.Temperature;
using MHApi.DrewsClasses;
using MHApi.NIControl;

namespace _24WellTrainer.ViewModels
{
    public class TempControlViewModel : ViewModelBase
    {

        #region Constructor

        public TempControlViewModel()
        {
            _tempSetpoint = 23;
            _temperature1 = _temperature2 = _temperature3 = _temperature4 = 12;
            if (IsInDesignMode)
                return;
            //read in PID parameters if configuration file present
            double tempKP, tempKI, tempVel;
            if (TempCalibViewModel.LoadPID(out tempKP, out tempKI, out tempVel))
            {
                _kP = tempKP;
                _kI = tempKI;
                _velocity = tempVel;
            }
            else
            {
                _kP = 0.0;
                _kI = 0.0;
                _velocity = 2;//2 degrees/second
            }
            //set up daq board
            _daq = new PCIe6323("dev1");
            //create thermistors
            _thermistor1 = new Thermistor(_daq, "ai0", ThermistorType.Omega);
            //We hook the Property changed event of the thermistors to display their temperature. In addition _thermistor1
            //takes the lead and updates the average temperature in the ProducerConsumer queue on the viewmodel
            _thermistor1.PropertyChanged += (sender, e) =>
            {
                Temperature1 = Math.Round(_thermistor1.Temperature, 2);
                if (MainViewModel.Current != null && MainViewModel.Current.IsRunning) BroadcastTemperature();
            };
            _thermistor2 = new Thermistor(_daq, "ai1", ThermistorType.Omega);
            _thermistor2.PropertyChanged += (sender, e) => { Temperature2 = Math.Round(_thermistor2.Temperature, 2); };
            _thermistor3 = new Thermistor(_daq, "ai2", ThermistorType.Omega);
            _thermistor3.PropertyChanged += (sender, e) => { Temperature3 = Math.Round(_thermistor3.Temperature, 2); };
            _thermistor4 = new Thermistor(_daq, "ai3", ThermistorType.Omega);
            _thermistor4.PropertyChanged += (sender, e) => { Temperature4 = Math.Round(_thermistor4.Temperature, 2); };

            //StartTemperatureControl();
            Current = this;
        }

        #endregion //Constructor

        #region Fields

        //The thermistors
        private Thermistor _thermistor1;
        private Thermistor _thermistor2;
        private Thermistor _thermistor3;
        private Thermistor _thermistor4;

        //The peltiers
        private Peltier _peltier1;
        private Peltier _peltier2;
        private Peltier _peltier3;
        private Peltier _peltier4;

        //The temperature controllers
        private TemperatureController _tempControl1;
        private TemperatureController _tempControl2;
        private TemperatureController _tempControl3;
        private TemperatureController _tempControl4;

        //The PID parameters
        private double _kP;
        private double _kI;
        private double _velocity;

        private PCIe6323 _daq;

        //The current temperatures
        private double _temperature1;
        private double _temperature2;
        private double _temperature3;
        private double _temperature4;

        private double _tempSetpoint;

        private bool _controlRunning;

        #endregion //Fields

        #region Properties

        /// <summary>
        /// The temperature reported by thermistor1
        /// </summary>
        public double Temperature1
        {
            get {return _temperature1;}
            set {_temperature1 = value;  RaisePropertyChanged("Temperature1");}
        }

        /// <summary>
        /// The temperature reported by thermistor2
        /// </summary>
        public double Temperature2
        {
            get {return _temperature2;}
            set  {_temperature2 = value; RaisePropertyChanged("Temperature2");}
        }

        /// <summary>
        /// The temperature reported by thermistor3
        /// </summary>
        public double Temperature3
        {
            get {return _temperature3;}
            set {_temperature3 = value; RaisePropertyChanged("Temperature3");}
        }

        /// <summary>
        /// The temperature reported by thermistor 4
        /// </summary>
        public double Temperature4
        {
            get {return _temperature4;}
            set { _temperature4 = value; RaisePropertyChanged("Temperature4"); }
        }

        /// <summary>
        /// The current desired temperature setpoint
        /// </summary>
        public double TempSetpoint
        {
            get {return _tempSetpoint;}
            set {UpdateTemperature(value); RaisePropertyChanged("TempSetpoint");}
        }

        /// <summary>
        /// Indicates if the temperature control is currently running
        /// </summary>
        public bool ControlRunning
        {
            get { return _controlRunning; }
            private set
            {
                _controlRunning = value;
                RaisePropertyChanged("ControlRunning");
                RaisePropertyChanged("ClampText");
            }
        }

        /// <summary>
        /// Interface to the ReadThreadReady event on the AI channel of the daq!
        /// </summary>
        public AutoResetEvent ReadThreadReady
        {
            get { return _daq.AI.ReadThreadReady;}
        }

        /// <summary>
        /// Reports from the mainviewmodel if an experiment is currently running
        /// </summary>
        public bool ExperimentRunning
        {
            get
            {
                if (MainViewModel.Current != null)
                    return MainViewModel.Current.IsRunning;
                else
                    return false;
            }
            set { RaisePropertyChanged("ExperimentRunning"); }
        }

        /// <summary>
        /// The NI board
        /// </summary>
        public PCIe6323 Daq
        {
            get { return _daq; }
        }

        /// <summary>
        /// The factor of the integral term in the TempControllers
        /// </summary>
        public double KI
        {
            get { return _kI; }
            set { _kI = value; RaisePropertyChanged("KI"); }
        }

        /// <summary>
        /// The factor of the proportional term in the TempControllers
        /// </summary>
        public double KP
        {
            get { return _kP; }
            set { _kP = value; RaisePropertyChanged("KP"); }
        }

        /// <summary>
        /// The update velocity towards the new target temp in the TempControllers
        /// </summary>
        public double UpdateVelocity
        {
            get { return _velocity; }
            set { _velocity = value; RaisePropertyChanged("UpdateVelocity"); }
        }

        /// <summary>
        /// The text for the button to engage
        /// preview temperature control
        /// </summary>
        public string ClampText
        {
            get { return ControlRunning ? "Shut off temperature control" : "Clamp to 28C"; }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Starts the AI reading service and hence temperature control
        /// </summary>
        public void StartTemperatureControl()
        {
            if (ControlRunning)
                return;
            try
            {
                _peltier1 = new Peltier("dev1", "ctr0", "PFI12", 1000, "Port1/line1");
                _peltier2 = new Peltier("dev1", "ctr1", "PFI13", 1000, "Port1/line2");
                _peltier3 = new Peltier("dev1", "ctr2", "PFI14", 1000, "Port1/line3");
                _peltier4 = new Peltier("dev1", "ctr3", "PFI15", 1000, "Port1/line4");

            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.Write(e);
                throw;
            }
            //Set up temperature controllers
            _tempControl1 = new TemperatureController(_thermistor1, _peltier1, 0, 0, _velocity, "TController1");
            _tempControl2 = new TemperatureController(_thermistor2, _peltier2, 0, 0, _velocity, "TController2");
            _tempControl3 = new TemperatureController(_thermistor3, _peltier3, 0, 0, _velocity, "TController3");
            _tempControl4 = new TemperatureController(_thermistor4, _peltier4, 0, 0, _velocity, "TController4");
            _tempControl1.TargetTemperature = _tempControl2.TargetTemperature = _tempControl3.TargetTemperature = _tempControl4.TargetTemperature = TempSetpoint;
            _tempControl1.KI = _tempControl2.KI = _tempControl3.KI = _tempControl4.KI = KI;
            _tempControl1.KP = _tempControl2.KP = _tempControl3.KP = _tempControl4.KP = KP;
            //knock out temp controller
            /*_tempControl4.KI = 0;
            _tempControl4.KP = 0;*/
            _tempControl1.Velocity = _tempControl2.Velocity = _tempControl3.Velocity = _tempControl4.Velocity = UpdateVelocity;
            //start daq
            _daq.AI.Start(2500, 100);
            //start temperature controllers
            ControlRunning = true;
            _tempControl1.Start();
            _tempControl2.Start();
            _tempControl3.Start();
            _tempControl4.Start();
        }

        /// <summary>
        /// Stops the AI reading service and hence temperature control
        /// </summary>
        public void StopTemperatureControl()
        {
            if (!ControlRunning)
                return;
            //stop temperature controllers
            _tempControl1.Stop();
            _tempControl2.Stop();
            _tempControl3.Stop();
            _tempControl4.Stop();
            //Dispose temperature controllers and peltiers
            _tempControl1.Dispose();
            _tempControl2.Dispose();
            _tempControl3.Dispose();
            _tempControl4.Dispose();
            _peltier1.Dispose();
            _peltier2.Dispose();
            _peltier3.Dispose();
            _peltier4.Dispose();
            //stop daq
            _daq.AI.Stop();
            ControlRunning = false;
        }

        /// <summary>
        /// Updates the set value of all 4 temperature controllers
        /// </summary>
        /// <param name="newTemp">The new desired set temperature</param>
        private void UpdateTemperature(double newTemp)
        {
            if (IsDisposed)
                throw new ObjectDisposedException("Attempted to update temperature on disposed TempControlViewModel");
            if (newTemp != _tempSetpoint)//only bother temperature controllers if temperature actually different
            {
                //The following is kinda stupid, maybe needs reworking - done because cooling takes VERY LONG
                //so a large error accumulates in the controllers
                if (newTemp > _tempSetpoint)
                {
                    _tempControl1.ResetIntegralOfError();
                    _tempControl2.ResetIntegralOfError();
                    _tempControl3.ResetIntegralOfError();
                    _tempControl4.ResetIntegralOfError();
                }
                //Update temperature controllers
                _tempControl1.TargetTemperature = newTemp;
                _tempControl2.TargetTemperature = newTemp;
                _tempControl3.TargetTemperature = newTemp;
                _tempControl4.TargetTemperature = newTemp;
                _tempSetpoint = newTemp;
            }
        }

        /// <summary>
        /// Adds the current temperatures to the producer consumer on the mainviemodel
        /// </summary>
        private void BroadcastTemperature()
        {
            //double avgTemp = (_thermistor1.Temperature + _thermistor2.Temperature+_thermistor3.Temperature+ _thermistor4.Temperature) / 4;
            MainViewModel.Current.TemperatureReadings.Produce(new TemperatureArray(_thermistor1.Temperature, _thermistor2.Temperature, _thermistor3.Temperature, _thermistor4.Temperature));
        }


        #endregion

        #region CommandSinks

        RelayCommand _clampClick;

        /// <summary>
        /// The command to clamp/unclamp the temperature
        /// in preview mode
        /// </summary>
        public ICommand ClampClick
        {
            get
            {
                if (_clampClick == null)
                {
                    _clampClick = new RelayCommand(param =>
                    {
                        if (ExperimentRunning) return;
                        if (ControlRunning)
                        { StopTemperatureControl(); TempSetpoint = 23; }
                        else
                        {
                            StartTemperatureControl(); TempSetpoint = 28;
                            ////Resend start trigger
                            _daq.AI.ReadThreadReady.WaitOne(-1);
                            TriggeredPWMTask.TriggerAll();
                            TriggeredPWMTask.ResetTrigger();
                            TempSetpoint = 28;
                        }
                    });
                }
                return _clampClick;
            }
        }

        RelayCommand _calibrateTempController;

        /// <summary>
        /// Command to start/stop an experiment
        /// </summary>
        public ICommand CalibrateTempController
        {
            get
            {
                if (_calibrateTempController == null)
                {
                    _calibrateTempController = new RelayCommand(param => StartCalibration());
                }
                return _calibrateTempController;
            }
        }

        /// <summary>
        /// Shows the dialog for temperature calibration and temporarily
        /// stops any temperature control and temperature acquisition
        /// </summary>
        private void StartCalibration()
        {
            if (ExperimentRunning)
            {
                System.Diagnostics.Debug.WriteLine("Attempted to start calibration while running an experiment");
                return;
            }
            //switch off temperature measurement
            StopTemperatureControl();
            //delete thermistors
            _thermistor1 = null;
            _thermistor2 = null;
            _thermistor3 = null;
            _thermistor4 = null;
            //Show calibration dialog
            TempCalibrationWindow wnd = new TempCalibrationWindow();
            wnd.ShowDialog();
            //Retake control of our thermistors
            _thermistor1 = new Thermistor(_daq, "ai0", ThermistorType.Omega);
            _thermistor1.PropertyChanged += (sender, e) =>
            {
                Temperature1 = Math.Round(_thermistor1.Temperature, 2);
                if (MainViewModel.Current != null && MainViewModel.Current.IsRunning) BroadcastTemperature();
            };
            _thermistor2 = new Thermistor(_daq, "ai1");
            _thermistor2.PropertyChanged += (sender, e) => { Temperature2 = Math.Round(_thermistor2.Temperature, 2); };
            _thermistor3 = new Thermistor(_daq, "ai2");
            _thermistor3.PropertyChanged += (sender, e) => { Temperature3 = Math.Round(_thermistor3.Temperature, 2); };
            _thermistor4 = new Thermistor(_daq, "ai3");
            _thermistor4.PropertyChanged += (sender, e) => { Temperature4 = Math.Round(_thermistor4.Temperature, 2); };

            ////Restart temperature control
            //StartTemperatureControl();
            ////Resend start trigger
            //_daq.AI.ReadThreadReady.WaitOne(-1);
            //TriggeredPWMTask.TriggerAll();
            //TriggeredPWMTask.ResetTrigger();

        }

        #endregion

        #region Cleanup

        protected override void Dispose(bool disposing)
        {
            if (_daq != null)
                _daq.Dispose();
            base.Dispose(disposing);
        }

        #endregion

        /// <summary>
        /// Static reference to the current temperatureControlViewModel to allow interaction with the temp control
        /// </summary>
        public static TempControlViewModel Current { get; private set; }
    }
}
