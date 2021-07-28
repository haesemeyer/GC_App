/*
Copyright 2011 Drew Robson
   Licensed under the MIT License, see License.txt.
   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
   IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
   FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
   AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
   LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
   OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
   SOFTWARE.
*/

using System;
using System.IO;
using System.Windows;
using System.ComponentModel;
using System.Diagnostics;
using MHApi.DrewsClasses;
using MHApi.Utilities;

namespace MHApi.DrewsClasses.Temperature{

    public enum ThermistorType{
        LFpolyimide,
        LFplastic,
        USsensor,
        test
    };

    public class Thermistor : PropertyChangeNotification {

        /// <summary>
        /// The temperature that is currently sensed by the thermistor
        /// </summary>
        double _temperature;

        /// <summary>
        /// The temperature that is currently sensed by the thermistor
        /// </summary>
        public double Temperature {
            get { return _temperature; }
            private set { _temperature = value; RaisePropertyChanged("Temperature"); }
        }

        /// <summary>
        /// The type of the current thermistor
        /// </summary>
        ThermistorType _type;

        /// <summary>
        /// The type of the current thermistor
        /// </summary>
        public ThermistorType Type
        {
            get { return _type; }
        }

        /// <summary>
        /// Constructs a new thermistor
        /// </summary>
        /// <param name="daq">The daq-board for reading the thermistor</param>
        /// <param name="channelName">The ai channel readint the thermistor bridge</param>
        /// <param name="type">The type of the thermistor</param>
        public Thermistor(PCIe6323 daq, string channelName, ThermistorType type) {
            _type = type;
            if (channelName.ToLower() == "ai0") {
                daq.AI.AI0.MinValue = -5;
                daq.AI.AI0.MaxValue = 5;
                daq.AI.AI0.Callback = (sampleIndex, samples) => {
                    int length = samples.Length;
                    double sum = 0;
                    for (int i = 0; i < length; i++)
                        sum += samples[i];
                    Temperature = ConvertRToT(ConvertVToR(sum / length),_type);
                };
            }
            if (channelName.ToLower() == "ai1") {
                daq.AI.AI1.MinValue = -5;
                daq.AI.AI1.MaxValue = 5;
                daq.AI.AI1.Callback = (sampleIndex, samples) => {
                    int length = samples.Length;
                    double sum = 0;
                    for (int i = 0; i < length; i++)
                        sum += samples[i];
                    Temperature = ConvertRToT(ConvertVToR(sum / length),_type);
                };
            }
            if (channelName.ToLower() == "ai2") {
                daq.AI.AI2.MinValue = -5;
                daq.AI.AI2.MaxValue = 5;
                daq.AI.AI2.Callback = (sampleIndex, samples) => {
                    int length = samples.Length;
                    double sum = 0;
                    for (int i = 0; i < length; i++)
                        sum += samples[i];
                    Temperature = ConvertRToT(ConvertVToR(sum / length),_type);
                };
            }
            if (channelName.ToLower() == "ai3") {
                daq.AI.AI3.MinValue = -5;
                daq.AI.AI3.MaxValue = 5;
                daq.AI.AI3.Callback = (sampleIndex, samples) => {
                    int length = samples.Length;
                    double sum = 0;
                    for (int i = 0; i < length; i++)
                        sum += samples[i];
                    Temperature = ConvertRToT(ConvertVToR(sum / length),_type);
                };
            }
            if (channelName.ToLower() == "ai4")
            {
                daq.AI.AI4.MinValue = -5;
                daq.AI.AI4.MaxValue = 5;
                daq.AI.AI4.Callback = (sampleIndex, samples) => {
                    int length = samples.Length;
                    double sum = 0;
                    for (int i = 0; i < length; i++)
                        sum += samples[i];
                    Temperature = ConvertRToT(ConvertVToR(sum / length), _type);
                };
            }
            if (channelName.ToLower() == "ai5")
            {
                daq.AI.AI5.MinValue = -5;
                daq.AI.AI5.MaxValue = 5;
                daq.AI.AI5.Callback = (sampleIndex, samples) => {
                    int length = samples.Length;
                    double sum = 0;
                    for (int i = 0; i < length; i++)
                        sum += samples[i];
                    Temperature = ConvertRToT(ConvertVToR(sum / length), _type);
                };
            }
        }

        #region Resistance <-> Temperature

        /// <summary>
        /// Resistance of each bridge part (as well as thermistor base resistance)
        /// </summary>
        const double RBridge = 1e4;
        /// <summary>
        /// Source voltage across each bridge and base resistor
        /// </summary>
        const double vS = 5.000;

        /// <summary>
        /// Uses the known bridge resistances and bridge voltage
        /// to convert a Voltage read on AI into a resistance
        /// </summary>
        /// <param name="v">The measured voltage</param>
        /// <returns>The resistance of the thermistor</returns>
        public static double ConvertVToR(double v) {
            return RBridge * (vS - 2 * v) / (vS + 2 * v);
        }

        /// <summary>
        /// Uses the temperature lookup table to convert a resistance into
        /// a thermistor temperature
        /// </summary>
        /// <param name="r">The measured resistance</param>
        /// <param name="type">The type of thermistor</param>
        /// <returns>The temperature of the thermistor</returns>
        public static double ConvertRToT(double r, ThermistorType type) {
            switch (type)
            {
                case ThermistorType.USsensor:
                    return -25.4255*Math.Log(r) + 259.4914;
                case ThermistorType.LFpolyimide:
                    return -27.33*Math.Log(r) + 275.80;
                case ThermistorType.LFplastic:
                    return -23.4081*Math.Log(r) + 240.9442;
                default:
                    return r; //(-15/RBridge)*r;
            }
        }

        #endregion
    }
}