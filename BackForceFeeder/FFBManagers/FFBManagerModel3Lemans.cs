﻿//#define CONSOLE_DUMP

using System;
using System.Globalization;
using System.Threading;

// Don't forget to add this
using BackForceFeeder.Utils;

namespace BackForceFeeder.FFBManagers
{
    /// <summary>
    /// Testing results on real hardware:
    /// only 8 level of effects (x = 0..7)
    /// 
    /// 0x00 sequence in selection screen?
    /// 0x1x no effect
    /// 0x2x = friction/clutch?
    /// 0x3x = spring
    /// 0x5x, 0x6X = constant torque turn left (pos)/right (neg)
    /// 0xFF = ping - keep previous effect
    /// 
    /// </summary>
    public class FFBManagerModel3Lemans :
        FFBManagerModel3
    {
        /// <summary>
        /// Known commands for Le mans DriveBoard ROM
        /// </summary>
        public enum LemansCMD : int
        {
            SEQU = 0x00,
            NO_EFFECT = 0x10,
            FRICTION = 0x20,
            SPRING = 0x30,
            TURNLEFT = 0x50,
            TURNRIGHT = 0x60,
        }



        /// <summary>
        /// Wheel sign is in opposite direction
        /// </summary>
        /// <param name="refreshPeriod_ms"></param>
        public FFBManagerModel3Lemans(int refreshPeriod_ms) :
            base(refreshPeriod_ms)
        {
            this.MAX_LEVEL = 0x7;
        }


        protected override void ComputeTrqFromAllEffects()
        {
            // Inputs:
            // - R=angular reference
            // - P=angular position
            // - W=angular velocity (W=dot(P)=dP/dt)
            // - A=angular accel (A=dot(W)=dW/dt=d2P/dt2)
            // output:
            // OutputEffectCommand

            // Take a snapshot of all values - convert time base to period
            EnterBarrier();
            double R = RefPosition_u;
            double P = FiltPosition_u_0;
            double W = FiltSpeed_u_per_s_0;
            double A = FiltAccel_u_per_s2_0;
            // Release the lock
            ExitBarrier();

            // For model 3 hardware, effects are directly translated to commands
            // for the driveboard

            // If using torque emulated mode, then effect will fill "AllTrq" and
            // the value will be converted to a left/right torque command
            double AllTrq = 0.0;
            bool translTrq2Cmd = false;
            bool isActiveEffect = false;
            for (int i = 0; i<RunningEffects.Length; i++) {
                // Skip effect not running
                if (!RunningEffects[i].IsRunning || RunningEffects[i]._LocalTime_ms < 0.0) {
                    continue;
                }
                double Trq = 0.0;
                switch (RunningEffects[i].Type) {
                    case EffectTypes.NO_EFFECT:
                        OutputEffectCommand = (long)LemansCMD.NO_EFFECT;
                        break;

                    case EffectTypes.CONSTANT_TORQUE: {
                            Trq = TrqFromConstant(i);
                            // Set flag to convert it to constant torque cmd
                            translTrq2Cmd = true;
                        }
                        break;

                    case EffectTypes.RAMP: {
                            if (ForceTrqForAllCommands || UseTrqEmulationForMissing) {
                                Trq = TrqFromRamp(i);
                                // Set flag to convert it to constant torque cmd
                                translTrq2Cmd = true;
                            } else {
                                // No effect
                                OutputEffectCommand = (long)LemansCMD.NO_EFFECT;
                            }
                        }
                        break;
                    case EffectTypes.FRICTION: {
                            if (ForceTrqForAllCommands) {
                                Trq = TrqFromFriction(i, W);
                                // Set flag to convert it to constant torque cmd
                                translTrq2Cmd = true;
                            } else {
                                // Translated to friction
                                // Select gain according to sign of velocity
                                if (W < 0)
                                    Trq = RunningEffects[i].NegativeCoef_u;
                                else
                                    Trq = RunningEffects[i].PositiveCoef_u;

                                // Scale in range and apply global gains before leaving
                                Trq = Math.Min(Math.Abs(Trq * RunningEffects[i].Gain * DeviceGain), 1.0);
                                // Trq is now in [0; 1]

                                // Friction strength – SendFriction
                                // 0x20: Disable - 0x21 = weakest - 0x2F = strongest
                                int strength = (int)(Trq * MAX_LEVEL);
                                OutputEffectCommand = (long)LemansCMD.FRICTION + strength;
                            }
                        }
                        break;
                    case EffectTypes.INERTIA: {
                            if (ForceTrqForAllCommands || UseTrqEmulationForMissing) {
                                Trq = TrqFromInertia(i, W, this.RawSpeed_u_per_s, A);
                                // Set flag to convert it to constant torque cmd
                                translTrq2Cmd = true;
                            } else {
                                // No effect
                                OutputEffectCommand = (long)LemansCMD.NO_EFFECT;
                            }
                        }
                        break;
                    case EffectTypes.SPRING: {
                            if (ForceTrqForAllCommands) {
                                Trq = TrqFromSpring(i, R, P);
                                // Set flag to convert it to constant torque cmd
                                translTrq2Cmd = true;
                                isActiveEffect = true;
                            } else {
                                // Translated to auto-centering
                                // Add Offset to reference position, then substract measure to
                                // get relative error sign
                                var error = (R + RunningEffects[i].Offset_u) - P;
                                // Select gain according to sign of error
                                // (maybe should be motion/velocity?)
                                if (error < 0)
                                    Trq = RunningEffects[i].NegativeCoef_u;
                                else
                                    Trq = RunningEffects[i].PositiveCoef_u;

                                // Scale in range and apply global gains before leaving
                                Trq = Math.Min(Math.Abs(Trq * RunningEffects[i].Gain * DeviceGain), 1.0);
                                // Trq is now in [0; 1]

                                // Set centering strength - auto-centering – SendSelfCenter
                                //
                                int strength = (int)(Trq* MAX_LEVEL);
                                OutputEffectCommand = (long)LemansCMD.SPRING + strength;
                            }
                        }
                        break;
                    case EffectTypes.DAMPER: {
                            if (ForceTrqForAllCommands || UseTrqEmulationForMissing) {
                                Trq = TrqFromDamper(i, W, this.RawSpeed_u_per_s, A);
                                // Set flag to convert it to constant torque cmd
                                translTrq2Cmd = true;
                            } else {
                                // No effect
                                OutputEffectCommand = (long)LemansCMD.NO_EFFECT;
                            }
                        }
                        break;


                    case EffectTypes.SINE: {
                            if (ForceTrqForAllCommands || UseTrqEmulationForMissing) {
                                Trq = TrqFromSine(i);
                                // Set flag to convert it to constant torque cmd
                                translTrq2Cmd = true;
                                // All done
                            } else {
                                // No effect
                                OutputEffectCommand = (long)LemansCMD.NO_EFFECT;
                            }
                        }
                        break;
                    case EffectTypes.SQUARE: {
                            if (ForceTrqForAllCommands || UseTrqEmulationForMissing) {
                                Trq = TrqFromSquare(i);
                                // Set flag to convert it to constant torque cmd
                                translTrq2Cmd = true;
                                // All done
                            } else {
                                // No effect
                                OutputEffectCommand = (long)LemansCMD.NO_EFFECT;
                            }
                        }
                        break;
                    case EffectTypes.TRIANGLE: {
                            if (ForceTrqForAllCommands || UseTrqEmulationForMissing) {
                                Trq = TrqFromTriangle(i);
                                // Set flag to convert it to constant torque cmd
                                translTrq2Cmd = true;
                                // All done
                            } else {
                                // No effect
                                OutputEffectCommand = (long)LemansCMD.NO_EFFECT;
                            }
                        }
                        break;
                    case EffectTypes.SAWTOOTHUP: {
                            if (ForceTrqForAllCommands || UseTrqEmulationForMissing) {
                                Trq = TrqFromSawtoothUp(i);
                                // Set flag to convert it to constant torque cmd
                                translTrq2Cmd = true;
                                // All done
                            } else {
                                // No effect
                                OutputEffectCommand = (long)LemansCMD.NO_EFFECT;
                            }
                        }
                        break;
                    case EffectTypes.SAWTOOTHDOWN: {
                            if (ForceTrqForAllCommands || UseTrqEmulationForMissing) {
                                Trq = TrqFromSawtoothDown(i);
                                // Set flag to convert it to constant torque cmd
                                translTrq2Cmd = true;
                                // All done
                            } else {
                                // No effect
                                OutputEffectCommand = (long)LemansCMD.NO_EFFECT;
                            }
                        }
                        break;

                    default:
                        break;
                }
                AllTrq += Trq * RunningEffects[i].Gain;
            }

            // If using Trq value, then convert to constant torque effect
            if (translTrq2Cmd) {
                // Permanent spring ? (except if spring already computer)
                if (!isActiveEffect && (PermanentSpring>0.0)) {
                    // Use effect Id 0
                    AllTrq += PermanentSpring*TrqFromSpring(0, R, P);
                }
                // Minimum damper ?
                if (isActiveEffect && (MinDamperForActive>0.0)) {
                    AllTrq += MinDamperForActive*TrqFromDamper(0, W, this.RawSpeed_u_per_s, A);
                }
                // Change sign of torque if inverted and apply gains
                AllTrq = TrqSign * Math.Sign(AllTrq) * Math.Pow(Math.Abs(AllTrq), PowerLaw) * DeviceGain * GlobalGain;
                // Scale in range
                AllTrq = Math.Max(Math.Min(AllTrq, 1.0), -1.0);
                // Save value
                OutputTorqueLevel = AllTrq;
                // Now convert to command
                TrqToCommand((int)LemansCMD.NO_EFFECT, (int)LemansCMD.TURNLEFT, (int)LemansCMD.TURNRIGHT);
            }

            this.CheckForEffectsDone();
        }


        /// <summary>
        /// Specific lemans
        /// </summary>
        protected override void State_INIT()
        {
            switch (Step) {
                case 0:
                    ResetAllEffects();
                    // Echo test
                    OutputEffectCommand = (int)GenericModel3CMD.PING;
                    TimeoutTimer.Restart();
                    GoToNextStep();
                    break;
                case 1:
                    if (TimeoutTimer.ElapsedMilliseconds>500) {
                        // Play sequence ?
                        OutputEffectCommand = (int)LemansCMD.SEQU;
                        TimeoutTimer.Restart();
                        GoToNextStep();
                    }
                    break;
                case 2:
                    if (TimeoutTimer.ElapsedMilliseconds>500) {
                        // 0xCB: reset board - SendStopAll
                        OutputEffectCommand = (int)LemansCMD.NO_EFFECT;
                        TimeoutTimer.Restart();
                        GoToNextStep();
                    }
                    break;
                case 3:
                    if (TimeoutTimer.ElapsedMilliseconds>100) {
                        // Maximum power set to 100%
                        OutputEffectCommand = (long)GenericModel3CMD.MOTOR_LEVEL100;
                        TimeoutTimer.Restart();
                        GoToNextStep();
                    }
                    break;
                case 4:
                    if (TimeoutTimer.ElapsedMilliseconds>100) {
                        GoToNextStep();
                    }
                    break;
                case 5:
                    TransitionTo(FFBStates.DEVICE_READY);
                    break;
            }
        }
        protected override void State_DISABLE()
        {
            switch (Step) {
                case 0:
                    OutputEffectCommand = (long)LemansCMD.NO_EFFECT;
                    break;
            }
        }
        protected override void State_READY()
        {
            switch (Step) {
                case 0:
                    OutputEffectCommand = (long)GenericModel3CMD.NO_EFFECT;
                    break;
            }
        }
    }
}


