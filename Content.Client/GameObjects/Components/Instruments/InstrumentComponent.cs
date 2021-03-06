using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.GameObjects.Components.Instruments;
using Content.Shared.Physics;
using JetBrains.Annotations;
using NFluidsynth;
using Robust.Shared.GameObjects;
using Robust.Client.Audio.Midi;
using Robust.Client.Player;
using Robust.Shared.Interfaces.Log;
using Robust.Shared.Interfaces.Network;
using Robust.Shared.Interfaces.Timing;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Players;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using Robust.Shared.ViewVariables;
using Logger = Robust.Shared.Log.Logger;
using MidiEvent = Robust.Shared.Audio.Midi.MidiEvent;
using Timer = Robust.Shared.Timers.Timer;

namespace Content.Client.GameObjects.Components.Instruments
{

    [RegisterComponent]
    public class InstrumentComponent : SharedInstrumentComponent
    {

        /// <summary>
        ///     Called when a midi song stops playing.
        /// </summary>
        public event Action OnMidiPlaybackEnded;

#pragma warning disable 649
        [Dependency] private readonly IMidiManager _midiManager;

        [Dependency] private readonly IGameTiming _gameTiming;

        [Dependency] private readonly IClientNetManager _netManager;
#pragma warning restore 649

        [CanBeNull]
        private IMidiRenderer _renderer;

        private byte _instrumentProgram = 1;

        private byte _instrumentBank = 0;

        private uint _sequenceDelay = 0;

        private uint _sequenceStartTick;

        /// <summary>
        ///     A queue of MidiEvents to be sent to the server.
        /// </summary>
        [ViewVariables]
        private readonly List<MidiEvent> _midiEventBuffer = new List<MidiEvent>();

        /// <summary>
        ///     Whether a midi song will loop or not.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public bool LoopMidi
        {
            get => _renderer?.LoopMidi ?? false;
            set
            {
                if (_renderer != null)
                {
                    _renderer.LoopMidi = value;
                }
            }
        }

        /// <summary>
        ///     Changes the instrument the midi renderer will play.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public byte InstrumentProgram
        {
            get => _instrumentProgram;
            set
            {
                _instrumentProgram = value;
                if (_renderer != null)
                {
                    _renderer.MidiProgram = _instrumentProgram;
                }
            }
        }

        /// <summary>
        ///     Changes the instrument bank the midi renderer will use.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public byte InstrumentBank
        {
            get => _instrumentBank;
            set
            {
                _instrumentBank = value;
                if (_renderer != null)
                {
                    _renderer.MidiBank = _instrumentBank;
                }
            }
        }

        /// <summary>
        ///     Whether there's a midi song being played or not.
        /// </summary>
        [ViewVariables]
        public bool IsMidiOpen => _renderer?.Status == MidiRendererStatus.File;

        /// <summary>
        ///     Whether the midi renderer is listening for midi input or not.
        /// </summary>
        [ViewVariables]
        public bool IsInputOpen => _renderer?.Status == MidiRendererStatus.Input;

        /// <summary>
        ///     Whether the midi renderer is alive or not.
        /// </summary>
        [ViewVariables]
        public bool IsRendererAlive => _renderer != null;

        public override void Initialize()
        {
            base.Initialize();
            IoCManager.InjectDependencies(this);
        }

        protected void SetupRenderer(bool fromStateChange = false)
        {
            if (IsRendererAlive) return;

            _sequenceDelay = 0;
            _sequenceStartTick = 0;
            _midiManager.OcclusionCollisionMask = (int) CollisionGroup.Impassable;
            _renderer = _midiManager.GetNewRenderer();

            if (_renderer != null)
            {
                _renderer.MidiBank = _instrumentBank;
                _renderer.MidiProgram = _instrumentProgram;
                _renderer.TrackingEntity = Owner;
                _renderer.OnMidiPlayerFinished += () =>
                {
                    OnMidiPlaybackEnded?.Invoke();
                    EndRenderer(fromStateChange);
                };
            }

            if (!fromStateChange)
            {
                SendNetworkMessage(new InstrumentStartMidiMessage());
            }
        }

        protected void EndRenderer(bool fromStateChange = false)
        {
            if (IsInputOpen)
            {
                CloseInput(fromStateChange);
                return;
            }

            if (IsMidiOpen)
            {
                CloseMidi(fromStateChange);
                return;
            }

            _renderer?.StopAllNotes();

            var renderer = _renderer;

            // We dispose of the synth two seconds from now to allow the last notes to stop from playing.
            Timer.Spawn(2000, () => { renderer?.Dispose(); });
            _renderer = null;
            _midiEventBuffer.Clear();

            if (!fromStateChange)
            {
                SendNetworkMessage(new InstrumentStopMidiMessage());
            }
        }

        protected override void Shutdown()
        {
            base.Shutdown();
            EndRenderer();
        }

        public override void ExposeData(ObjectSerializer serializer)
        {
            base.ExposeData(serializer);
            serializer.DataField(ref _instrumentProgram, "program", (byte) 1);
            serializer.DataField(ref _instrumentBank, "bank", (byte) 0);
        }

        public override void HandleNetworkMessage(ComponentMessage message, INetChannel channel, ICommonSession session = null)
        {
            base.HandleNetworkMessage(message, channel, session);

            switch (message)
            {
                case InstrumentMidiEventMessage midiEventMessage:
                    if (IsRendererAlive)
                    {
                        // If we're the ones sending the MidiEvents, we ignore this message.
                        if (IsInputOpen || IsMidiOpen) break;
                    }
                    else
                    {
                        // if we haven't started or finished some sequence
                        if (_sequenceStartTick == 0)
                        {
                            // we may have arrived late
                            SetupRenderer(true);
                        }

                        // might be our own notes after we already finished playing
                        return;
                    }

                    if (_sequenceStartTick <= 0)
                    {
                        _sequenceStartTick = midiEventMessage.MidiEvent
                            .Min(x => x.Tick) - 1;
                    }

                    var sqrtLag = MathF.Sqrt(_netManager.ServerChannel.Ping / 1000f);
                    var delay = (uint) (_renderer!.SequencerTimeScale * (.2 + sqrtLag));
                    var delta = delay - _sequenceStartTick;

                    _sequenceDelay = Math.Max(_sequenceDelay, delta);

                    var currentTick = _renderer.SequencerTick;

                    // ReSharper disable once ForCanBeConvertedToForeach
                    for (var i = 0; i < midiEventMessage.MidiEvent.Length; i++)
                    {
                        var ev = midiEventMessage.MidiEvent[i];
                        var scheduled = ev.Tick + _sequenceDelay;

                        if (scheduled <= currentTick)
                        {
                            _sequenceDelay += currentTick - ev.Tick;
                            scheduled = ev.Tick + _sequenceDelay;
                        }


                        _renderer?.ScheduleMidiEvent(ev, scheduled, true);
                    }

                    break;
                case InstrumentStartMidiMessage startMidiMessage:
                {
                    SetupRenderer(true);
                    break;
                }
                case InstrumentStopMidiMessage stopMidiMessage:
                {
                    EndRenderer(true);
                    break;
                }
            }
        }

        public override void HandleComponentState(ComponentState curState, ComponentState nextState)
        {
            base.HandleComponentState(curState, nextState);
            if (!(curState is InstrumentState state)) return;

            if (state.Playing)
            {
                SetupRenderer(true);
            }
            else
            {
                EndRenderer(true);
            }
        }

        /// <inheritdoc cref="MidiRenderer.OpenInput"/>
        public bool OpenInput()
        {
            SetupRenderer();

            if (_renderer != null && _renderer.OpenInput())
            {
                _renderer.OnMidiEvent += RendererOnMidiEvent;
                return true;
            }

            return false;
        }

        /// <inheritdoc cref="MidiRenderer.CloseInput"/>
        public bool CloseInput(bool fromStateChange = false)
        {
            if (_renderer == null || !_renderer.CloseInput())
            {
                return false;
            }

            EndRenderer(fromStateChange);
            return true;
        }

        /// <inheritdoc cref="MidiRenderer.OpenMidi(string)"/>
        public bool OpenMidi(string filename)
        {
            SetupRenderer();

            if (_renderer == null || !_renderer.OpenMidi(filename))
            {
                return false;
            }

            _renderer.OnMidiEvent += RendererOnMidiEvent;
            return true;
        }

        /// <inheritdoc cref="MidiRenderer.CloseMidi"/>
        public bool CloseMidi(bool fromStateChange = false)
        {
            if (_renderer == null || !_renderer.CloseMidi())
            {
                return false;
            }

            EndRenderer(fromStateChange);
            return true;
        }

        /// <summary>
        ///     Called whenever the renderer receives a midi event.
        /// </summary>
        /// <param name="midiEvent">The received midi event</param>
        private void RendererOnMidiEvent(MidiEvent midiEvent)
        {
            // avoid of out-of-band, unimportant or unsupported events
            switch (midiEvent.Type)
            {
                case 0x80: // NOTE_OFF
                case 0x90: // NOTE_ON
                case 0xa0: // KEY_PRESSURE
                case 0xb0: // CONTROL_CHANGE
                case 0xd0: // CHANNEL_PRESSURE
                case 0xe0: // PITCH_BEND
                    break;
                default:
                    return;
            }

            _midiEventBuffer.Add(midiEvent);
        }

        private TimeSpan _lastMeasured = TimeSpan.MinValue;

        private int _sentWithinASec = 0;

        private static readonly TimeSpan OneSecAgo = TimeSpan.FromSeconds(-1);

        private static readonly Comparer<MidiEvent> SortMidiEventTick
            = Comparer<MidiEvent>.Create((x, y)
                => x.Tick.CompareTo(y.Tick));

        public override void Update(float delta)
        {
            if (!IsMidiOpen && !IsInputOpen) return;

            var now = _gameTiming.RealTime;
            var oneSecAGo = now.Add(OneSecAgo);

            if (_lastMeasured <= oneSecAGo)
            {
                _lastMeasured = now;
                _sentWithinASec = 0;
            }

            if (_midiEventBuffer.Count == 0) return;

            var max = Math.Min(MaxMidiEventsPerBatch, MaxMidiEventsPerSecond - _sentWithinASec);

            if (max <= 0)
            {
                // hit event/sec limit, have to lag the batch or drop events
                return;
            }

            // fix cross-fade events generating retroactive events
            // also handle any significant backlog of events after midi finished

            _midiEventBuffer.Sort(SortMidiEventTick);
            var bufferTicks = IsRendererAlive && _renderer!.Status != MidiRendererStatus.None
                ? _renderer.SequencerTimeScale * .2f
                : 0;
            var bufferedTick = IsRendererAlive
                ? _renderer!.SequencerTick - bufferTicks
                : int.MaxValue;

            var events = _midiEventBuffer
                .TakeWhile(x => x.Tick < bufferedTick)
                .Take(max)
                .ToArray();

            var eventCount = events.Length;

            if (eventCount == 0) return;

            SendNetworkMessage(new InstrumentMidiEventMessage(events));

            _sentWithinASec += eventCount;

            _midiEventBuffer.RemoveRange(0, eventCount);
        }

    }

}
