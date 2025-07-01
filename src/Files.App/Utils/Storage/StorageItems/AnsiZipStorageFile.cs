
using Files.Shared.Helpers;
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using SevenZip;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.Win32;
using IO = System.IO;

namespace Files.App.Utils.Storage
{
    public sealed partial class AnsiZipStorageFile : BaseStorageFile, IPasswordProtectedItem
	{
		private readonly string containerPath;
		private readonly BaseStorageFile backingFile;
        private Encoding encoding;

        public override string Path { get; }
		public override string Name { get; }
		public override string DisplayName => Name;
        public override string ContentType => "application/octet-stream";
		public override string FileType => IO.Path.GetExtension(Name);
		public override string FolderRelativeId => $"0\\{Name}";

        public override string DisplayType
		{
			get
			{
				var itemType = Strings.File.GetLocalizedResource();
				if (Name.Contains('.', StringComparison.Ordinal))
				{
					itemType = FileType.Trim('.') + " " + itemType;
				}
				return itemType;
			}
		}

        public override DateTimeOffset DateCreated { get; }
		public override Windows.Storage.FileAttributes Attributes => Windows.Storage.FileAttributes.Normal | Windows.Storage.FileAttributes.ReadOnly;

		private IStorageItemExtraProperties properties;
		public override IStorageItemExtraProperties Properties => properties ??= new BaseBasicStorageItemExtraProperties(this);

        public StorageCredential Credentials { get; set; } = new();

		public Func<IPasswordProtectedItem, Task<StorageCredential>> PasswordRequestedCallback { get; set; }

        public AnsiZipStorageFile(string path, string containerPath)
		{
			Name = IO.Path.GetFileName(path.TrimEnd('\\', '/'));
			Path = path;
			this.containerPath = containerPath;
		}
        public AnsiZipStorageFile(string path, string containerPath, BaseStorageFile backingFile) : this(path, containerPath)
			=> this.backingFile = backingFile;
        public AnsiZipStorageFile(string path, string containerPath, ArchiveFileInfo entry) : this(path, containerPath)
			=> DateCreated = entry.CreationTime == DateTime.MinValue ? DateTimeOffset.MinValue : entry.CreationTime;
		public AnsiZipStorageFile(string path, string containerPath, ArchiveFileInfo entry, BaseStorageFile backingFile) : this(path, containerPath, entry)
			=> this.backingFile = backingFile;

        public override IAsyncOperation<StorageFile> ToStorageFileAsync()
			=> StorageFile.CreateStreamedFileAsync(Name, ZipDataStreamingHandler(Path), null);

        public static IAsyncOperation<BaseStorageFile> FromPathAsync(string path)
		{
			if (!FileExtensionHelpers.IsBrowsableZipFile(path, out var ext))
			{
				return Task.FromResult<BaseStorageFile>(null).AsAsyncOperation();
			}
			var marker = path.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
			if (marker is not -1)
			{
				var containerPath = path.Substring(0, marker + ext.Length);
				if (path == containerPath)
				{
					return Task.FromResult<BaseStorageFile>(null).AsAsyncOperation(); // Root
				}
				if (CheckAccess(containerPath))
				{
					return Task.FromResult<BaseStorageFile>(new ZipStorageFile(path, containerPath)).AsAsyncOperation();
				}
			}
			return Task.FromResult<BaseStorageFile>(null).AsAsyncOperation();
		}

        public override bool IsEqual(IStorageItem item) => item?.Path == Path;
		public override bool IsOfType(StorageItemTypes type) => type is StorageItemTypes.File;

        public override IAsyncOperation<BaseStorageFolder> GetParentAsync() => throw new NotSupportedException();
		public override IAsyncOperation<BaseBasicProperties> GetBasicPropertiesAsync() => GetBasicProperties().AsAsyncOperation();

        public override IAsyncOperation<IRandomAccessStream> OpenAsync(FileAccessMode accessMode)
		{
            return AsyncInfo.Run((cancellationToken) => SafetyExtensions.Wrap<IRandomAccessStream>(async () =>
			{
                bool rw = accessMode is FileAccessMode.ReadWrite;
				if (Path == containerPath)
				{
					if (backingFile is not null)
					{
						return await backingFile.OpenAsync(accessMode);
					}

					var file = Win32Helper.OpenFileForRead(containerPath, rw);
					return file.IsInvalid ? null : new FileStream(file, rw ? FileAccess.ReadWrite : FileAccess.Read).AsRandomAccessStream();
				}
                if (!rw)
				{
                    ZipFile zipFile = await OpenZipFileAsync();
					if (zipFile is null)
					{
						return null;
					}
					var entry = zipFile.Cast<ZipEntry>().FirstOrDefault(x => System.IO.Path.Combine(containerPath, x.Name) == Path);
					if (entry != null)
					{
						var ms = new MemoryStream();
						var inputStream = zipFile.GetInputStream(entry);
						await inputStream.CopyToAsync(ms);
						ms.Position = 0;
						return new NonSeekableRandomAccessStreamForRead(ms, entry.Size);
					}
					return null;
                }
				throw new NotSupportedException("Can't open zip file as RW");
            }, ((IPasswordProtectedItem)this).RetryWithCredentialsAsync));
        }
		public override IAsyncOperation<IRandomAccessStream> OpenAsync(FileAccessMode accessMode, StorageOpenOptions options)
			=> OpenAsync(accessMode);

		public override IAsyncOperation<IRandomAccessStreamWithContentType> OpenReadAsync()
		{
            return AsyncInfo.Run((cancellationToken) => SafetyExtensions.Wrap<IRandomAccessStreamWithContentType>(async () =>
            {
                if (Path == containerPath)
                {
                    if (backingFile is not null)
                    {
                        return await backingFile.OpenReadAsync();
                    }

                    var hFile = Win32Helper.OpenFileForRead(containerPath);
                    return hFile.IsInvalid ? null : new StreamWithContentType(new FileStream(hFile, FileAccess.Read).AsRandomAccessStream());
                }

                using (var zipFile = await OpenZipFileAsync())
                {
                    if (zipFile is null)
                    {
                        return null;
                    }

                    var entry = zipFile.GetEntry(Path.Substring(containerPath.Length + 1));
                    if (entry != null)
                    {
                        var ms = new MemoryStream();
                        var inputStream = zipFile.GetInputStream(entry);
                        await inputStream.CopyToAsync(ms);
                        ms.Position = 0;
                        var nsStream = new NonSeekableRandomAccessStreamForRead(ms, entry.Size);
                        return new StreamWithContentType(nsStream);
                    }
                    return null;
                }
            }, ((IPasswordProtectedItem)this).RetryWithCredentialsAsync));
        }
        
		public override IAsyncOperation<IInputStream> OpenSequentialReadAsync()
        {
            return AsyncInfo.Run((cancellationToken) => SafetyExtensions.Wrap<IInputStream>(async () =>
            {
                if (Path == containerPath)
                {
                    if (backingFile is not null)
                    {
                        return await backingFile.OpenSequentialReadAsync();
                    }

                    var hFile = Win32Helper.OpenFileForRead(containerPath);
                    return hFile.IsInvalid ? null : new FileStream(hFile, FileAccess.Read).AsInputStream();
                }

                using (var zipFile = await OpenZipFileAsync())
                {
                    if (zipFile is null)
                    {
                        return null;
                    }

                    var entry = zipFile.GetEntry(Path.Substring(containerPath.Length + 1));
                    if (entry != null)
                    {
                        var ms = new MemoryStream();
                        var inputStream = zipFile.GetInputStream(entry);
                        await inputStream.CopyToAsync(ms);
                        ms.Position = 0;
                        return new NonSeekableRandomAccessStreamForRead(ms, entry.Size);
                    }
                    return null;
                }
            }, ((IPasswordProtectedItem)this).RetryWithCredentialsAsync));
        }

		public override IAsyncOperation<StorageStreamTransaction> OpenTransactedWriteAsync()
            => throw new NotSupportedException();
        public override IAsyncOperation<StorageStreamTransaction> OpenTransactedWriteAsync(StorageOpenOptions options)
            => throw new NotSupportedException();

		public override IAsyncOperation<BaseStorageFile> CopyAsync(IStorageFolder destinationFolder)
            => CopyAsync(destinationFolder, Name, NameCollisionOption.FailIfExists);
        public override IAsyncOperation<BaseStorageFile> CopyAsync(IStorageFolder destinationFolder, string desiredNewName)
            => CopyAsync(destinationFolder, desiredNewName, NameCollisionOption.FailIfExists);
        public override IAsyncOperation<BaseStorageFile> CopyAsync(IStorageFolder destinationFolder, string desiredNewName, NameCollisionOption option)
        {
            return AsyncInfo.Run((cancellationToken) => SafetyExtensions.Wrap<BaseStorageFile>(async () =>
            {
                using (var zipFile = await OpenZipFileAsync())
                {
                    if (zipFile is null)
                    {
                        return null;
                    }

                    var entry = zipFile.GetEntry(Path.Substring(containerPath.Length + 1));
                    if (entry != null)
                    {
                        var destFolder = destinationFolder.AsBaseStorageFolder();

                        if (destFolder is ICreateFileWithStream cwsf)
                        {
                            var ms = new MemoryStream();
                            var inputStream = zipFile.GetInputStream(entry);
                            await inputStream.CopyToAsync(ms);
                            ms.Position = 0;
                            using var inStream = new NonSeekableRandomAccessStreamForRead(ms, entry.Size);
                            return await cwsf.CreateFileAsync(inStream.AsStreamForRead(), desiredNewName, option.Convert());
                        }
                        else
                        {
                            var destFile = await destFolder.CreateFileAsync(desiredNewName, option.Convert());
                            await using var outStream = await destFile.OpenStreamForWriteAsync();
                            var inputStream = zipFile.GetInputStream(entry);
                            await SafetyExtensions.WrapAsync(() => inputStream.CopyToAsync(outStream), async (_, exception) =>
                            {
                                await destFile.DeleteAsync();
                                throw exception;
                            });
                            return destFile;
                        }
                    }
                    return null;
                }
            }, ((IPasswordProtectedItem)this).RetryWithCredentialsAsync));
        }
        public override IAsyncAction CopyAndReplaceAsync(IStorageFile fileToReplace)
        {
            return AsyncInfo.Run((cancellationToken) => SafetyExtensions.WrapAsync(async () =>
            {
                using (var zipFile = await OpenZipFileAsync())
                {
                    if (zipFile is null)
                    {
                        return;
                    }

                    var entry = zipFile.GetEntry(Path.Substring(containerPath.Length + 1));
                    if (entry != null)
                    {
                        using var hDestFile = fileToReplace.CreateSafeFileHandle(FileAccess.ReadWrite);
                        await using (var outStream = new FileStream(hDestFile, FileAccess.Write))
                        {
                            var inputStream = zipFile.GetInputStream(entry);
                            await inputStream.CopyToAsync(outStream);
                        }
                    }
                }
            }, ((IPasswordProtectedItem)this).RetryWithCredentialsAsync));
        }

		public override IAsyncAction MoveAsync(IStorageFolder destinationFolder)
            => throw new NotSupportedException();
        public override IAsyncAction MoveAsync(IStorageFolder destinationFolder, string desiredNewName)
            => throw new NotSupportedException();
        public override IAsyncAction MoveAsync(IStorageFolder destinationFolder, string desiredNewName, NameCollisionOption option)
            => throw new NotSupportedException();
        public override IAsyncAction MoveAndReplaceAsync(IStorageFile fileToReplace)
            => throw new NotSupportedException();

		public override IAsyncAction RenameAsync(string desiredName) => RenameAsync(desiredName, NameCollisionOption.FailIfExists);
        public override IAsyncAction RenameAsync(string desiredName, NameCollisionOption option)
        {
            return AsyncInfo.Run((cancellationToken) => SafetyExtensions.WrapAsync(async () =>
            {
                if (Path == containerPath)
                {
                    if (backingFile is not null)
                    {
                        await backingFile.RenameAsync(desiredName, option);
                    }
                    else
                    {
                        var fileName = IO.Path.Combine(IO.Path.GetDirectoryName(Path), desiredName);
                        PInvoke.MoveFileFromApp(Path, fileName);
                    }
                }
                else
                {
                    var entryNameInZip = Path.Substring(containerPath.Length + 1);
                    using (var ms = new MemoryStream())
                    {
                        using (var archiveStream = await OpenZipFileStreamAsync(FileAccessMode.Read))
                        {
                            using (var zf = new ZipFile(archiveStream, true, zipEncoding))
                            {
                                zf.RenameEntry(entryNameInZip, IO.Path.Combine(IO.Path.GetDirectoryName(entryNameInZip), desiredName));
                                zf.CommitUpdate();
                                await zf.WriteStream.CopyToAsync(ms);
                            }
                        }

                        await using (var archiveStream = await OpenZipFileStreamAsync(FileAccessMode.ReadWrite))
                        {
                            ms.Position = 0;
                            await ms.CopyToAsync(archiveStream);
                            await ms.FlushAsync();
                            archiveStream.SetLength(archiveStream.Position);
                        }
                    }
                    Name = desiredName; // 更新 Name 属性
                    Path = IO.Path.Combine(containerPath, IO.Path.GetDirectoryName(Path.Substring(containerPath.Length + 1)), desiredName); // 更新 Path 属性
                }
            }, ((IPasswordProtectedItem)this).RetryWithCredentialsAsync));
        }

        public override IAsyncAction DeleteAsync() => DeleteAsync(StorageDeleteOption.Default);
        public override IAsyncAction DeleteAsync(StorageDeleteOption option)
        {
            return AsyncInfo.Run((cancellationToken) => SafetyExtensions.WrapAsync(async () =>
            {
                if (Path == containerPath)
                {
                    if (backingFile is not null)
                    {
                        await backingFile.DeleteAsync();
                    }
                    else if (option == StorageDeleteOption.PermanentDelete)
                    {
                        PInvoke.DeleteFileFromApp(Path);
                    }
                    else
                    {
                        throw new NotSupportedException("Moving to recycle bin is not supported.");
                    }
                }
                else
                {
                    var entryNameInZip = Path.Substring(containerPath.Length + 1);
                    using (var ms = new MemoryStream())
                    {
                        using (var archiveStream = await OpenZipFileStreamAsync(FileAccessMode.Read))
                        {
                            using (var zf = new ZipFile(archiveStream, true, zipEncoding))
                            {
                                zf.DeleteEntry(entryNameInZip);
                                zf.CommitUpdate();
                                await zf.WriteStream.CopyToAsync(ms);
                            }
                        }

                        await using (var archiveStream = await OpenZipFileStreamAsync(FileAccessMode.ReadWrite))
                        {
                            ms.Position = 0;
                            await ms.CopyToAsync(archiveStream);
                            await ms.FlushAsync();
                            archiveStream.SetLength(archiveStream.Position);
                        }
                    }
                }
            }, ((IPasswordProtectedItem)this).RetryWithCredentialsAsync));
        }

		public override IAsyncOperation<StorageItemThumbnail> GetThumbnailAsync(ThumbnailMode mode)
            => Task.FromResult<StorageItemThumbnail>(null).AsAsyncOperation();
        public override IAsyncOperation<StorageItemThumbnail> GetThumbnailAsync(ThumbnailMode mode, uint requestedSize)
            => Task.FromResult<StorageItemThumbnail>(null).AsAsyncOperation();
		public override IAsyncOperation<StorageItemThumbnail> GetThumbnailAsync(ThumbnailMode mode, uint requestedSize, ThumbnailOptions options)
			=> Task.FromResult<StorageItemThumbnail>(null).AsAsyncOperation();
		
		private IAsyncOperation<ZipFile> OpenZipFileAsync()
		{
			return AsyncInfo.Run<ZipFile>(async (cancellationToken) =>
			{
				var zipStream = await OpenZipFileAsync(FileAccessMode.Read);
				return zipStream is not null ? new ZipFile(zipStream, false, StringCodec.FromEncoding(encoding)) : null;
			});
		}

		private IAsyncOperation<Stream> OpenZipFileAsync(FileAccessMode accessMode)
		{
			return AsyncInfo.Run<Stream>(async (cancellationToken) =>
			{
				bool readWrite = accessMode == FileAccessMode.ReadWrite;
				if (backingFile is not null)
				{
					return (await backingFile.OpenAsync(accessMode)).AsStream();
				}
				else
				{
					var hFile = Win32Helper.OpenFileForRead(containerPath, readWrite);
					if (hFile.IsInvalid)
					{
						return null;
					}
					return new FileStream(hFile, readWrite ? FileAccess.ReadWrite : FileAccess.Read);
				}
			});
		}
    
        private StreamedFileDataRequestedHandler ZipDataStreamingHandler(string name)
		{
			return async request =>
			{
                try
                {
                    using ZipFile zipFile = await OpenZipFileAsync();
                    if (zipFile is null)
                    {
                        request.FailAndClose(StreamedFileFailureMode.CurrentlyUnavailable);
                        return;
                    }
					//zipFile.IsStreamOwner = true;
					//var entry = zipFile.ArchiveFileData.FirstOrDefault(x => System.IO.Path.Combine(containerPath, x.FileName) == name);
					var entry = zipFile.GetEntry(System.IO.Path.GetRelativePath(containerPath, name));
                    if (entry is null)
                    {
                        request.FailAndClose(StreamedFileFailureMode.CurrentlyUnavailable);
                    }
                    else
                    {
                        await using (var outStream = request.AsStreamForWrite())
                        {
							var buffer = new byte[4096];
							using (var zipStream = zipFile.GetInputStream(entry))
							{
								StreamUtils.Copy(zipStream, outStream, buffer);
							}

						}
                        request.Dispose();
                    }
                }
                catch
                {
                    request.FailAndClose(StreamedFileFailureMode.Failed);
                }
            }
        }
    }
}