
using System;
using System.Collections.Generic;
using System.Linq;
using Foundation;
using AppKit;
using QTKit;
using System.IO;
using CoreImage;
using System.Text;
using ObjCRuntime;

namespace QTRecorder
{
	public partial class QTRDocument : AppKit.NSDocument
	{
		QTCaptureSession session;
		QTCaptureDeviceInput videoDeviceInput, audioDeviceInput;
		QTCaptureMovieFileOutput movieFileOutput;
		QTCaptureAudioPreviewOutput audioPreviewOutput;

		QTCaptureDevice[] videoDevices, audioDevices;

		NSTimer audioLevelTimer;

		public override string WindowNibName {
			get {
				return "QTRDocument";
			}
		}

		// Link any co-dependent keys
		[Export ("keyPathsForValuesAffectingHasRecordingDevice")]
		public static NSSet keyPathsForValuesAffectingHasRecordingDevice ()
		{
			return new NSSet ("SelectedVideoDevice", "SelectedAudioDevice");
		}

		[Export ("keyPathsForValuesAffectingControllableDevice")]
		public static NSSet keyPathsForValuesAffectingControllableDevice ()
		{
			return new NSSet ("SelectedVideoDevice");
		}

		[Export ("keyPathsForValuesAffectingSelectedVideoDeviceProvidesAudio")]
		public static NSSet keyPathsForValuesAffectingSelectedVideoDeviceProvidesAudio ()
		{
			return new NSSet ("SelectedVideoDevice");
		}

		[Export ("keyPathsForValuesAffectingMediaFormatSummary")]
		public static NSSet keyPathsForValuesAffectingMediaFormatSummary ()
		{
			return new NSSet ("SelectedVideoDevice", "SelectedAudioDevice");
		}

		public override void WindowControllerDidLoadNib (NSWindowController windowController)
		{
			base.WindowControllerDidLoadNib (windowController);

			// Create session
			session = new QTCaptureSession ();

			// Attach preview to session
			captureView.CaptureSession = session;
			captureView.WillDisplayImage = WillDisplayImage;

			// Attach outputs to session
			movieFileOutput = new QTCaptureMovieFileOutput ();

			movieFileOutput.WillStartRecording += WillStartRecording;
			movieFileOutput.DidStartRecording += DidStartRecording;
			movieFileOutput.ShouldChangeOutputFile = ShouldChangeOutputFile;
			movieFileOutput.MustChangeOutputFile += MustChangeOutputFile;

			// These ones we care about, some notifications
			movieFileOutput.WillFinishRecording += WillFinishRecording;
			movieFileOutput.DidFinishRecording += DidFinishRecording;

			NSError error;
			session.AddOutput (movieFileOutput, out error);

			audioPreviewOutput = new QTCaptureAudioPreviewOutput ();
			audioPreviewOutput.Volume = 0;
			session.AddOutput (audioPreviewOutput, out error);
			
			if (VideoDevices.Length > 0)
				SelectedVideoDevice = VideoDevices [0];
			
			if (AudioDevices.Length > 0)
				SelectedAudioDevice = AudioDevices [0];

			session.StartRunning ();

			// events: devices added/removed
			AddObserver (QTCaptureDevice.WasConnectedNotification, DevicesDidChange);
			AddObserver (QTCaptureDevice.WasDisconnectedNotification, DevicesDidChange);

			// events: connection format changes
			AddObserver (QTCaptureConnection.FormatDescriptionDidChangeNotification, FormatDidChange);
			AddObserver (QTCaptureConnection.FormatDescriptionWillChangeNotification, FormatWillChange);

			AddObserver (QTCaptureDevice.AttributeWillChangeNotification, AttributeWillChange);
			AddObserver (QTCaptureDevice.AttributeDidChangeNotification, AttributeDidChange);

			audioLevelTimer = NSTimer.CreateRepeatingScheduledTimer (0.1, UpdateAudioLevels);
		}

		#region Device selection

		void DevicesDidChange (NSNotification notification)
		{
			RefreshDevices ();
		}

		void RefreshDevices ()
		{
			WillChangeValue ("VideoDevices");
			videoDevices = QTCaptureDevice.GetInputDevices (QTMediaType.Video).Concat (QTCaptureDevice.GetInputDevices (QTMediaType.Muxed)).ToArray ();
			DidChangeValue ("VideoDevices");

			WillChangeValue ("AudioDevices");
			audioDevices = QTCaptureDevice.GetInputDevices (QTMediaType.Sound);
			DidChangeValue ("AudioDevices");

			if (!videoDevices.Contains (SelectedVideoDevice))
				SelectedVideoDevice = null;

			if (!audioDevices.Contains (SelectedAudioDevice))
				SelectedAudioDevice = null;
		}

		[Export ("VideoDevices")]
		public QTCaptureDevice [] VideoDevices {
			get {
				if (videoDevices == null)
					RefreshDevices ();
				return videoDevices;
			}
		}

		[Export ("AudioDevices")]
		public QTCaptureDevice [] AudioDevices {
			get {
				if (audioDevices == null)
					RefreshDevices ();
				return audioDevices;
			}
		}

		[Export ("SelectedVideoDevice")]
		public QTCaptureDevice SelectedVideoDevice { 
			get {
				return videoDeviceInput != null ? videoDeviceInput.Device : null;
			}
			set {
				if (videoDeviceInput != null) {
					// Remove the old device input from the session and close the device
					session.RemoveInput (videoDeviceInput);
					videoDeviceInput.Device.Close ();
					videoDeviceInput.Dispose ();
					videoDeviceInput = null;
				}

				if (value != null) {
					NSError err;
					if (!value.Open (out err)) {
						NSAlert.WithError (err).BeginSheet (Window, () => {
						});
						return;
					}

					// Create a device input for the device and add it to the session
					videoDeviceInput = new QTCaptureDeviceInput (value);

					if (!session.AddInput (videoDeviceInput, out err)) {
						NSAlert.WithError (err).BeginSheet (Window, () => {
						});
						videoDeviceInput.Dispose ();
						videoDeviceInput = null;
						value.Close ();
						return;
					}
				}

				// If the video device provides audio, do not use a new audio device
				if (SelectedVideoDeviceProvidesAudio)
					SelectedAudioDevice = null;
			}
		}

		[Export ("SelectedAudioDevice")]
		public QTCaptureDevice SelectedAudioDevice { 
			get {
				return audioDeviceInput != null ? audioDeviceInput.Device : null;
			}
			set {
				if (audioDeviceInput != null) {
					session.RemoveInput (audioDeviceInput);
					audioDeviceInput.Device.Close ();
					audioDeviceInput.Dispose ();
					audioDeviceInput = null;
				}

				if (value == null || SelectedVideoDeviceProvidesAudio)
					return;

				NSError err;

				// try to open
				if (!value.Open (out err)) {
					NSAlert.WithError (err).BeginSheet (Window, () => {
					});
					return;
				}

				// Create a device input for the device and add it to the session
				audioDeviceInput = new QTCaptureDeviceInput (value);
				if (session.AddInput (audioDeviceInput, out err))
					return;

				NSAlert.WithError (err).BeginSheet (Window, () => {
				});
				audioDeviceInput.Dispose ();
				audioDeviceInput = null;
				value.Close ();
			}
		}

		bool SelectedVideoDeviceProvidesAudio {
			get {
				var x = SelectedVideoDevice;
				if (x == null)
					return false;
				
				return x.HasMediaType (QTMediaType.Muxed) || x.HasMediaType (QTMediaType.Sound);
			}
		}

		#endregion

		#region Capture and recording

		[Export ("AudioPreviewOutput")]
		public QTCaptureAudioPreviewOutput AudioPreviewOutput {
			get {
				return audioPreviewOutput;
			}
		}

		[Export ("HasRecordingDevice")]
		public bool HasRecordingDevice {
			get {
				return videoDeviceInput != null || audioDeviceInput != null;
			}
		}

		[Export ("Recording")]
		public bool Recording {
			get {
				return movieFileOutput != null && movieFileOutput.OutputFileUrl != null;
			}
			set {
				if (value == Recording)
					return;

				var tempName = string.Format ("{0}.mov", Path.GetTempFileName ());
				NSUrl url = value ? NSUrl.FromFilename (tempName) : null;
				movieFileOutput.RecordToOutputFile (url);
			}
		}

		void WillStartRecording (object sender, QTCaptureFileUrlEventArgs e)
		{
			Console.WriteLine ("Will start recording");
		}

		void DidStartRecording (object sender, QTCaptureFileUrlEventArgs e)
		{
			Console.WriteLine ("Started Recording");
		}

		bool ShouldChangeOutputFile (QTCaptureFileOutput captureOutput, NSUrl outputFileURL, QTCaptureConnection[] connections, NSError reason)
		{
			// Should change the file on error
			Console.WriteLine (reason.LocalizedDescription);
			return false;
		}

		void MustChangeOutputFile (object sender, QTCaptureFileErrorEventArgs e)
		{
			Console.WriteLine ("Must change file due to error");
		}

		void WillFinishRecording (object sender, QTCaptureFileErrorEventArgs e)
		{
			Console.WriteLine ("Will finish recording to {0} due to error {1}", e.OutputFileURL.Description, e.Reason);
			InvokeOnMainThread (() => WillChangeValue ("Recording"));
		}

		void DidFinishRecording (object sender, QTCaptureFileErrorEventArgs e)
		{
			Console.WriteLine ("Recorded {0} bytes duration {1}", movieFileOutput.RecordedFileSize, movieFileOutput.RecordedDuration);
			DidChangeValue ("Recording");

			// TODO: https://bugzilla.xamarin.com/show_bug.cgi?id=27691
			IntPtr library = Dlfcn.dlopen ("/System/Library/Frameworks/QTKit.framework/QTKit", 0);
			var key = Dlfcn.GetStringConstant (library, "QTCaptureConnectionAttributeWillChangeNotification");
			if (e.Reason != null && !((NSNumber)e.Reason.UserInfo [key]).BoolValue) {
				NSAlert.WithError (e.Reason).BeginSheet (Window, () => {
				});
				return;
			}

			var save = NSSavePanel.SavePanel;
			save.AllowedFileTypes = new string[] { "mov" };
			save.CanSelectHiddenExtension = true;
			save.BeginSheet (WindowForSheet, code => {
				NSError err2;
				if (code == (int)NSPanelButtonType.Ok) {
					if (NSFileManager.DefaultManager.Move (e.OutputFileURL, save.Url, out err2))
						NSWorkspace.SharedWorkspace.OpenUrl (save.Url);
					else
						save.OrderOut (this);
				} else {
					NSFileManager.DefaultManager.Remove (e.OutputFileURL.Path, out err2);
				}
			});
		}

		#endregion

		#region Video preview filter

		// Not available until we bind CIFilter
		readonly string[] filterNames = new string [] {
			"CIKaleidoscope", "CIGaussianBlur",	"CIZoomBlur",
			"CIColorInvert", "CISepiaTone", "CIBumpDistortion",
			"CICircularWrap", "CIHoleDistortion", "CITorusLensDistortion",
			"CITwirlDistortion", "CIVortexDistortion", "CICMYKHalftone",
			"CIColorPosterize", "CIDotScreen", "CIHatchedScreen",
			"CIBloom", "CICrystallize", "CIEdges",
			"CIEdgeWork", "CIGloom", "CIPixellate",
		};
		static NSString filterNameKey = new NSString ("filterName");
		static NSString localizedFilterKey = new NSString ("localizedName");

		// Creates descriptions that can be accessed with Key/Values
		NSDictionary[] descriptions;

		[Export ("VideoPreviewFilterDescriptions")]
		NSDictionary [] VideoPreviewFilterDescriptions {
			get {
				descriptions = descriptions ?? filterNames.Select (name => new NSDictionary (filterNameKey, name, localizedFilterKey, CIFilter.FilterLocalizedName (name)))
						.ToArray ();
				return descriptions;
			}
		}

		NSDictionary description;

		[Export ("VideoPreviewFilterDescription")]
		NSDictionary VideoPreviewFilterDescription {
			get {
				return description;
			}
			set {
				if (value == description)
					return;
				description = value;
				captureView.NeedsDisplay = true;
			}
		}

		CIImage WillDisplayImage (QTCaptureView view, CIImage image)
		{
			if (description == null)
				return image;
			
			var selectedFilter = (NSString)description [filterNameKey];

			var filter = CIFilter.FromName (selectedFilter);
			filter.SetDefaults ();
			filter.Image = image;

			return filter.OutputImage;
		}

		#endregion

		#region Media format summary

		[Export ("MediaFormatSummary")]
		public string MediaFormatSummary {
			get {
				var sb = new StringBuilder ();

				if (videoDeviceInput != null) {
					foreach (var c in videoDeviceInput.Connections)
						sb.AppendLine (c.FormatDescription.LocalizedFormatSummary);
				}

				if (audioDeviceInput != null) {
					foreach (var c in audioDeviceInput.Connections)
						sb.AppendLine (c.FormatDescription.LocalizedFormatSummary);
				}

				return sb.ToString ();
			}
		}

		void FormatWillChange (NSNotification n)
		{
			var owner = ((QTCaptureConnection)n.Object).Owner;
			Console.WriteLine (owner);
			if (owner == videoDeviceInput || owner == audioDeviceInput)
				WillChangeValue ("MediaFormatSummary");
		}

		void FormatDidChange (NSNotification n)
		{
			var owner = ((QTCaptureConnection)n.Object).Owner;
			Console.WriteLine (owner);
			if (owner == videoDeviceInput || owner == audioDeviceInput)
				DidChangeValue ("MediaFormatSummary");
		}

		#endregion

		#region UI updating

		void UpdateAudioLevels (NSTimer timer)
		{
			// Get the mean audio level from the movie file output's audio connections
			float totalDecibels = 0f;

			QTCaptureConnection connection = null;
			int i = 0;
			int numberOfPowerLevels = 0;	// Keep track of the total number of power levels in order to take the mean

			// TODO: https://bugzilla.xamarin.com/show_bug.cgi?id=27702
			IntPtr library = Dlfcn.dlopen ("/System/Library/Frameworks/QTKit.framework/QTKit", 0);
			NSString soundType = Dlfcn.GetStringConstant (library, "QTMediaTypeSound");

			var connections = movieFileOutput.Connections;
			for (i = 0; i < connections.Length; i++) {
				connection = connections [i];

				if (connection.MediaType == soundType) {
					// TODO: https://bugzilla.xamarin.com/show_bug.cgi?id=27708 Use typed property
					NSArray powerLevelsNative = (NSArray)connection.GetAttribute (QTCaptureConnection.AudioAveragePowerLevelsAttribute);
					NSNumber[] powerLevels = NSArray.FromArray<NSNumber> (powerLevelsNative);

					int j = 0;
					int powerLevelCount = powerLevels.Length;

					for (j = 0; j < powerLevelCount; j++) {
						totalDecibels += powerLevels [j].FloatValue;
						numberOfPowerLevels++;
					}
				}
			}

			if (numberOfPowerLevels > 0)
				audioLevelIndicator.FloatValue = 20 * (float)Math.Pow (10, 0.05 * (totalDecibels / numberOfPowerLevels));
			else
				audioLevelIndicator.FloatValue = 0;
		}

		#endregion

		#region Device controls

		[Export ("ControllableDevice")]
		public QTCaptureDevice ControllableDevice {
			get {
				if (SelectedVideoDevice == null)
					return null;

				if (SelectedVideoDevice.AvcTransportControl == null)
					return null;

				if (SelectedVideoDevice.IsAvcTransportControlReadOnly)
					return null;

				return SelectedVideoDevice;
			}
		}

		[Export ("DevicePlaying")]
		public bool DevicePlaying {
			get {
				var device = ControllableDevice;
				if (device == null)
					return false;

				QTCaptureDeviceTransportControl controls = device.AvcTransportControl;
				if (controls == null)
					return false;

				return controls.Speed.Value == QTCaptureDeviceControlsSpeed.NormalForward && controls.PlaybackMode == QTCaptureDevicePlaybackMode.Playing;
			}
			set {
				var device = ControllableDevice;
				if (device == null)
					return;

				device.AvcTransportControl = new QTCaptureDeviceTransportControl () {
					Speed = value ? QTCaptureDeviceControlsSpeed.NormalForward : QTCaptureDeviceControlsSpeed.Stopped,
					PlaybackMode = QTCaptureDevicePlaybackMode.Playing
				};
			}
		}

		partial void StopDevice (NSObject sender)
		{
			var device = ControllableDevice;
			if (device == null)
				return;

			device.AvcTransportControl = new QTCaptureDeviceTransportControl () {
				Speed = QTCaptureDeviceControlsSpeed.Stopped,
				PlaybackMode = QTCaptureDevicePlaybackMode.NotPlaying
			};
		}

		[Export ("DeviceRewinding")]
		public bool DeviceRewinding {
			get {
				return GetDeviceSpeed (x => x < QTCaptureDeviceControlsSpeed.Stopped);
			}
			set {
				SetDeviceSpeed (value, QTCaptureDeviceControlsSpeed.FastReverse);
			}
		}

		[Export ("DeviceFastForwarding")]
		public bool DeviceFastForwarding {
			get {
				return GetDeviceSpeed (x => x > QTCaptureDeviceControlsSpeed.Stopped);
			}
			set {
				SetDeviceSpeed (value, QTCaptureDeviceControlsSpeed.FastForward);
			}
		}

		bool GetDeviceSpeed (Func<QTCaptureDeviceControlsSpeed?, bool> g)
		{
			var device = ControllableDevice;
			if (device == null)
				return false;

			var control = device.AvcTransportControl;
			if (control == null)
				return false;

			return g (control.Speed);
		}

		void SetDeviceSpeed (bool value, QTCaptureDeviceControlsSpeed speed)
		{
			var device = ControllableDevice;
			if (device == null)
				return;

			var control = device.AvcTransportControl;
			if (control == null)
				return;

			control.Speed = value ? speed : QTCaptureDeviceControlsSpeed.Stopped;
			device.AvcTransportControl = control;
		}

		void AttributeWillChange (NSNotification n)
		{
			// TODO: https://bugzilla.xamarin.com/show_bug.cgi?id=27709
			IntPtr library = Dlfcn.dlopen ("/System/Library/Frameworks/QTKit.framework/QTKit", 0);
			var transportAttribute = Dlfcn.GetStringConstant (library, "QTCaptureDeviceAVCTransportControlsAttribute");

			var changedKey = (NSString)n.UserInfo [QTCaptureDevice.ChangedAttributeKey];
			if (n.Object == ControllableDevice && changedKey == transportAttribute) {
				WillChangeValue ("DevicePlaying");
				WillChangeValue ("DeviceFastforwarding");
				WillChangeValue ("DeviceRewinding");
			}
		}

		void AttributeDidChange (NSNotification n)
		{
			// TODO: https://bugzilla.xamarin.com/show_bug.cgi?id=27709
			IntPtr library = Dlfcn.dlopen ("/System/Library/Frameworks/QTKit.framework/QTKit", 0);
			var transportAttribute = Dlfcn.GetStringConstant (library, "QTCaptureDeviceAVCTransportControlsAttribute");

			var changedKey = (NSString)n.UserInfo [QTCaptureDevice.ChangedAttributeKey];
			if (n.Object == ControllableDevice && changedKey == transportAttribute) {
				DidChangeValue ("DevicePlaying");
				DidChangeValue ("DeviceFastforwarding");
				DidChangeValue ("DeviceRewinding");
			}
		}

		#endregion

		List<NSObject> notifications = new List<NSObject> ();

		void AddObserver (NSString key, Action<NSNotification> notification)
		{
			notifications.Add (NSNotificationCenter.DefaultCenter.AddObserver (key, notification));
		}

		NSWindow Window { 
			get {
				return WindowControllers [0].Window;
			}
		}

		protected override void Dispose (bool disposing)
		{
			if (disposing) {
				NSNotificationCenter.DefaultCenter.RemoveObservers (notifications);
				notifications = null;
			}
			base.Dispose (disposing);
		}
	}
}

