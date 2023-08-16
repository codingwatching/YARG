﻿using System;
using System.Collections.Generic;
using UnityEngine;

namespace YARG.Gameplay
{
    public class BeatEventManager : MonoBehaviour
    {
        public readonly struct Info
        {
            /// <summary>
            /// Quarter notes would be <c>1f / 4f</c>, whole notes <c>1f</c>, etc.
            /// </summary>
            public readonly float Note;

            /// <summary>
            /// The offset of the event in seconds.
            /// </summary>
            public readonly float Offset;

            public Info(float note, float offset)
            {
                Note = note;
                Offset = offset;
            }
        }

        private class State
        {
            public readonly Info Info;

            public double LastTime;

            public State(Info info)
            {
                Info = info;
            }
        }

        private GameManager _gameManager;

        private int _currentTimeSigIndex;
        private int _nextTimeSigIndex = 1;

        private int _currentTempoIndex;
        private int _nextTempoIndex = 1;

        private readonly Dictionary<Action, State> _states = new();

        private void Awake()
        {
            _gameManager = GetComponent<GameManager>();
        }

        public void Subscribe(Action action, Info info)
        {
            _states.Add(action, new State(info));
        }

        public void Unsubscribe(Action action)
        {
            _states.Remove(action);
        }

        public void ResetTimers()
        {
            foreach (var (_, state) in _states)
            {
                state.LastTime = 0;
            }

            _currentTimeSigIndex = 0;
            _nextTimeSigIndex = 1;

            _currentTempoIndex = 0;
            _nextTempoIndex = 1;
        }

        private void Update()
        {
            // Skip while loading
            if (_gameManager.Chart is null) return;

            var sync = _gameManager.Chart.SyncTrack;

            // Update the time signature indices
            var timeSigs = sync.TimeSignatures;
            while (_nextTimeSigIndex < timeSigs.Count && timeSigs[_nextTimeSigIndex].Time < _gameManager.InputTime)
            {
                _currentTimeSigIndex++;
                _nextTimeSigIndex++;
            }

            // Get the time signature (important for beat length)
            var currentTimeSig = timeSigs[_currentTimeSigIndex];

            // Update the tempo indices
            var tempos = sync.Tempos;
            while (_nextTempoIndex < tempos.Count && tempos[_nextTempoIndex].Time < _gameManager.InputTime)
            {
                _currentTempoIndex++;
                _nextTempoIndex++;
            }

            // Get the seconds per measure
            var currentTempo = tempos[_currentTempoIndex];
            float secondsPerWhole = currentTempo.SecondsPerBeat * (4f / currentTimeSig.Denominator) * 4f;

            // Update per action now
            foreach (var (action, state) in _states)
            {
                // Get seconds per specified note
                float secondsPerNote = secondsPerWhole * state.Info.Note;

                // Call action
                if (state.LastTime + secondsPerNote <= _gameManager.SongTime + state.Info.Offset)
                {
                    action();
                    state.LastTime = _gameManager.SongTime;
                }
            }
        }
    }
}