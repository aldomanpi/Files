// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using System.IO;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Files.App.Utils.Storage
{
	public static class MtpHelpers
	{
		private static readonly ConcurrentDictionary<string, DeviceLookup> _deviceParsingNames = new(StringComparer.OrdinalIgnoreCase);

		private static readonly TimeSpan NegativeCacheDuration = TimeSpan.FromSeconds(5);

		/// <summary>
		/// Resolves a <c>\\?\DeviceName\path</c> to a shell Portable Devices namespace path
		/// so that shell APIs like <see cref="IShellItemImageFactory"/> work correctly.
		/// </summary>
		public static string? ResolveMtpShellPath(string mtpPath)
		{
			if (mtpPath.Length <= 4)
				return null;

			var withoutPrefix = mtpPath.AsSpan(4);
			var sep = withoutPrefix.IndexOf('\\');
			var deviceName = (sep >= 0 ? withoutPrefix[..sep] : withoutPrefix).ToString();

			// A device that wasn't enumerable yet has to be retried, but only after the negative entry expires
			if (!_deviceParsingNames.TryGetValue(deviceName, out var lookup) ||
				(lookup.ParsingName is null && DateTime.UtcNow - lookup.ProbedAt >= NegativeCacheDuration))
			{
				string? probedName;
				unsafe
				{
					probedName = FindDeviceParsingName(deviceName);
				}

				lookup = new DeviceLookup(probedName, DateTime.UtcNow);
				_deviceParsingNames[deviceName] = lookup;
			}

			var parsingName = lookup.ParsingName;

			return parsingName is null ? null
				: sep >= 0 ? Path.Combine(parsingName, withoutPrefix[(sep + 1)..].ToString())
				: parsingName;
		}

		private unsafe static string? FindDeviceParsingName(string deviceName)
		{
			HRESULT hr = PInvoke.SHGetKnownFolderItem(FOLDERID.FOLDERID_ComputerFolder, KNOWN_FOLDER_FLAG.KF_FLAG_DEFAULT, null, out IShellItem computerFolderItem);
			if (hr.ThrowIfFailedOnDebug().Failed)
				return null;

			hr = computerFolderItem.BindToHandler(null, PInvoke.BHID_EnumItems, out IEnumShellItems? pEnum);
			if (hr.ThrowIfFailedOnDebug().Failed || pEnum is null)
				return null;

			string? prefixMatch = null;
			IShellItem[] children = new IShellItem[1];
			while (true)
			{
				if (pEnum.Next(children) != HRESULT.S_OK)
					break;

				IShellItem pChild = children[0];

				pChild.GetDisplayName(SIGDN.SIGDN_NORMALDISPLAY, out var szName);
				var name = szName.ToString();
				PInvoke.CoTaskMemFree(szName.Value);

				if (name is null || !deviceName.StartsWith(name, StringComparison.OrdinalIgnoreCase))
					continue;

				var isExactMatch = deviceName.Equals(name, StringComparison.OrdinalIgnoreCase);
				if (!isExactMatch && prefixMatch is not null)
					continue;

				pChild.GetDisplayName(SIGDN.SIGDN_DESKTOPABSOLUTEPARSING, out var szParsing);
				var result = szParsing.ToString();
				PInvoke.CoTaskMemFree(szParsing.Value);

				// A shorter device name can be enumerated first, so only an exact match ends the search
				if (isExactMatch)
					return result;

				prefixMatch = result;
			}

			return prefixMatch;
		}

		/// <summary>
		/// Represents the outcome of a device lookup, a null <c>ParsingName</c> marks a failed probe.
		/// </summary>
		private readonly record struct DeviceLookup(string? ParsingName, DateTime ProbedAt);
	}
}
