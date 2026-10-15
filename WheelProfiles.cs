using System;
using System.Collections.Generic;
using System.Linq;

namespace FFBeast.SimHubPlugin
{
    public sealed class WheelProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string Game { get; set; } = "";
        public string Car { get; set; } = "";
        public WheelSettings Settings { get; set; } = new WheelSettings();
        public WheelProfile Clone() => new WheelProfile
        { Id = Id, Name = Name, Game = Game, Car = Car, Settings = Settings.Clone() };
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name))
                throw new ArgumentException("Profile needs a name and ID.");
            if (!string.IsNullOrWhiteSpace(Car) && string.IsNullOrWhiteSpace(Game))
                throw new ArgumentException("A car-specific profile also needs a game name.");
            if (Settings == null) throw new ArgumentException("Profile settings are missing.");
            Settings.Validate();
        }
        public override string ToString() => Name;
    }

    public sealed class ProfileConfiguration
    {
        public bool AutoApply { get; set; }
        public string SelectedProfileId { get; set; } = "";
        public List<WheelProfile> Profiles { get; set; } = new List<WheelProfile>();
        public ProfileConfiguration Clone() => new ProfileConfiguration
        {
            AutoApply = AutoApply, SelectedProfileId = SelectedProfileId,
            Profiles = Profiles.Select(p => p.Clone()).ToList()
        };
        public void Validate()
        {
            if (Profiles == null) throw new ArgumentException("Profile list is missing.");
            foreach (var profile in Profiles) { if (profile == null) throw new ArgumentException("Invalid profile."); profile.Validate(); }
            if (Profiles.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Profiles.Count)
                throw new ArgumentException("Profile IDs must be unique.");
        }
        public WheelProfile? Selected => Profiles.FirstOrDefault(p => p.Id == SelectedProfileId);
        public WheelProfile? Match(string game, string car) => Profiles
            .Where(p => !string.IsNullOrWhiteSpace(p.Game) && string.Equals(p.Game, game, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(p.Car) || string.Equals(p.Car, car, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(p => !string.IsNullOrWhiteSpace(p.Car)).FirstOrDefault();
        public WheelProfile? Cycle(int direction)
        {
            if (Profiles.Count == 0) return null;
            int index = Profiles.FindIndex(p => p.Id == SelectedProfileId);
            index = index < 0 ? (direction < 0 ? Profiles.Count - 1 : 0) : (index + direction + Profiles.Count) % Profiles.Count;
            SelectedProfileId = Profiles[index].Id;
            return Profiles[index];
        }
    }

    /// <summary>Optional profile selection once per game/car context, never a force effect.</summary>
    public sealed class AutoProfileSelector
    {
        private string? _attemptedContext;
        public WheelProfile? Select(ProfileConfiguration config, string game, string car,
            bool gameRunning, bool replay, double speedKmh)
        {
            if (!config.AutoApply || !gameRunning) { _attemptedContext = null; return null; }
            if (replay || string.IsNullOrWhiteSpace(car) || double.IsNaN(speedKmh) || double.IsInfinity(speedKmh) || Math.Abs(speedKmh) > 1)
                return null;
            var profile = config.Match(game, car);
            if (profile == null) return null;
            string context = game + "\0" + car + "\0" + profile.Id;
            if (context == _attemptedContext) return null;
            _attemptedContext = context;
            return profile.Clone();
        }
        public void RetryBusyRequest() { _attemptedContext = null; }
    }
}
