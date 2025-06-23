using Files.Shared.Helpers;
using ICSharpCode.SharpZipLib.Zip;
using ICSharpCode.SharpZipLib.Core; // For StringCodec and StreamDataSource
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text; // For Encoding
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Search;
using Windows.Win32;
using IO = System.IO;

namespace Files.App.Utils.Storage
{
    public sealed partial class AnsiZipStorageFolder : BaseStorageFolder, ICreateFileWithStream, IPasswordProtectedItem
    {
        private readonly string containerPath;
        private BaseStorageFile backingFile;
        private readonly Encoding encoding; // For handling non-UTF8 filenames

        public override string Path { get; }
        public override string Name { get; }
        public override string DisplayName => Name;
        public override string DisplayType => Strings.Folder.GetLocalizedResource(); // Assuming Strings.Folder exists
        public override string FolderRelativeId => $"0\\{Name}";

        public override DateTimeOffset DateCreated { get; }
        public override Windows.Storage.FileAttributes Attributes => Windows.Storage.FileAttributes.Directory;
        public override IStorageItemExtraProperties Properties => new BaseBasicStorageItemExtraProperties(this);

        public StorageCredential Credentials { get; set; } = new();
        public Func<IPasswordProtectedItem, Task<StorageCredential>> PasswordRequestedCallback { get; set; }

        // Default to system's default ANSI encoding if not specified, or UTF-8 for broader compatibility.
        // Consider making this more explicit, e.g., Encoding.GetEncoding(0) for system ANSI, or a specific code page.
        public static Encoding DefaultZipEncoding { get; set; } = Encoding.UTF8; // Or Encoding.Default for system ANSI

        public AnsiZipStorageFolder(string path, string containerPath, Encoding encoding = null)
        {
            Name = IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            Path = path;
            containerPath = containerPath;
            encoding = encoding ?? DefaultZipEncoding;
            // DateCreated for root is typically the file's date, handled in GetBasicPropertiesAsync
        }

        public AnsiZipStorageFolder(string path, string containerPath, BaseStorageFile backingFile, Encoding encoding = null)
            : this(path, containerPath, encoding)
            => backingFile = backingFile;

        public AnsiZipStorageFolder(string path, string containerPath, ZipEntry entry, Encoding encoding = null)
            : this(path, containerPath, encoding)
            => DateCreated = entry.DateTime;

        public AnsiZipStorageFolder(BaseStorageFile backingFile, Encoding encoding = null)
        {
            if (string.IsNullOrEmpty(backingFile.Path))
            {
                throw new ArgumentException("Backing file Path cannot be null");
            }
            Name = IO.Path.GetFileName(backingFile.Path.TrimEnd('\\', '/'));
            Path = backingFile.Path;
            containerPath = backingFile.Path;
            backingFile = backingFile;
            encoding = encoding ?? DefaultZipEncoding;
            // DateCreated will be the backing file's creation date, fetched via GetBasicPropertiesAsync
        }

        public AnsiZipStorageFolder(string path, string containerPath, ZipEntry entry, BaseStorageFile backingFile, Encoding encoding = null)
            : this(path, containerPath, entry, encoding)
            => backingFile = backingFile;


        public static bool IsZipPath(string path, bool includeRoot = true)
        {
            if (!FileExtensionHelpers.IsBrowsableZipFile(path, out var ext)) // Assuming this helper checks for .zip, .001 etc.
            {
                return false;
            }
            var marker = path.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
            if (marker is -1)
            {
                return false;
            }
            marker += ext.Length;
            return (marker == path.Length && includeRoot && !IO.Directory.Exists(path + "\\")) // Check for directory with .zip in name
                   || (marker < path.Length && path[marker] is '\\' && !IO.Directory.Exists(path));
        }

        public async Task<long> GetUncompressedSize()
        {
            long uncompressedSize = 0;
            ZipFile zipFile = null;
            try
            {
                zipFile = await OpenZipFileAsync(FileAccess.Read);
                if (zipFile is not null)
                {
                    foreach (ZipEntry entry in zipFile)
                    {
                        if (!entry.IsDirectory)
                        {
                            uncompressedSize += entry.Size;
                        }
                    }
                }
            }
            catch (Exception ex) // Catch more specific ZipException if needed
            {
                // Log error or handle
                System.Diagnostics.Debug.WriteLine($"Error getting uncompressed size: {ex.Message}");
            }
            finally
            {
                zipFile?.Close();
            }
            return uncompressedSize;
        }

        private static readonly ConcurrentDictionary<string, Task<bool>> defaultAppDict = new();
        public static async Task<bool> CheckDefaultZipApp(string filePath)
        {
            Func<Task<bool>> queryFileAssoc = async () =>
            {
                var assoc = await Win32Helper.GetFileAssociationAsync(filePath); // Assuming Win32Helper exists
                if (assoc is not null)
                {
                    return assoc == Package.Current.Id.FamilyName
                           || assoc.EndsWith("Files.App\\Files.exe", StringComparison.OrdinalIgnoreCase)
                           || assoc.Equals(IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase);
                }
                return true; // Default to true if no association found or to avoid blocking
            };
            var ext = IO.Path.GetExtension(filePath)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) return true; // Cannot determine for extension-less files
            return await defaultAppDict.GetOrAdd(ext, _ => queryFileAssoc());
        }

        public static IAsyncOperation<BaseStorageFolder> FromPathAsync(string path, Encoding encoding = null)
        {
            return AsyncInfo.Run<BaseStorageFolder>(async (cancellationToken) =>
            {
                if (!FileExtensionHelpers.IsBrowsableZipFile(path, out var ext))
                {
                    return null;
                }
                var marker = path.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                if (marker is not -1)
                {
                    var containerPath = path.Substring(0, marker + ext.Length);
                    if (await CheckAccessAsync(containerPath, encoding ?? DefaultZipEncoding))
                    {
                        return new AnsiZipStorageFolder(path, containerPath, encoding ?? DefaultZipEncoding);
                    }
                }
                return null;
            });
        }

        public static IAsyncOperation<BaseStorageFolder> FromStorageFileAsync(BaseStorageFile file, Encoding encoding = null)
            => AsyncInfo.Run<BaseStorageFolder>(async (cancellationToken) =>
                await CheckAccessAsync(file, encoding ?? DefaultZipEncoding) ? new AnsiZipStorageFolder(file, encoding ?? DefaultZipEncoding) : null);


        public override IAsyncOperation<StorageFolder> ToStorageFolderAsync() => throw new NotSupportedException();

        public override bool IsEqual(IStorageItem item) => item?.Path == Path;
        public override bool IsOfType(StorageItemTypes type) => type == StorageItemTypes.Folder;

        public override IAsyncOperation<IndexedState> GetIndexedStateAsync() => Task.FromResult(IndexedState.NotIndexed).AsAsyncOperation();

        public override IAsyncOperation<BaseStorageFolder> GetParentAsync()
        {
            // A root zip folder doesn't have a parent in this context.
            // A subfolder within a zip could, but that logic is more complex
            // and depends on how you want to navigate "up" from a virtual folder.
            // For simplicity, an exception or null might be returned.
            // The original threw NotSupportedException.
            return Task.FromException<BaseStorageFolder>(new NotSupportedException()).AsAsyncOperation();
        }


        private async Task<BaseBasicProperties> GetFolderPropertiesFromEntry()
        {
            ZipFile zipFile = null;
            try
            {
                zipFile = await OpenZipFileAsync(FileAccess.Read);
                if (zipFile is null) return new BaseBasicProperties();

                // Path for a folder entry in zip usually ends with "/"
                // We need to find the entry that matches this folder's path relative to the container.
                var relativePath = IO.Path.GetRelativePath(containerPath, Path.TrimEnd('\\', '/')).Replace('\\', '/');
                if (!string.IsNullOrEmpty(relativePath) && !relativePath.EndsWith('/'))
                {
                    relativePath += "/";
                }
                else if (string.IsNullOrEmpty(relativePath)) // Root of archive, represented by backing file
                {
                     if (backingFile != null) return await backingFile.GetBasicPropertiesAsync();
                     var fileInfo = new FileInfo(containerPath);
                     return new BaseBasicProperties(
                         (ulong)fileInfo.Length, // Size
                         fileInfo.LastWriteTimeUtc, // DateModified
                         DateTimeOffset.MinValue, // ItemDate | typically not used for folders like this
                         fileInfo.CreationTimeUtc // DateCreated
                     );
                }


                ZipEntry entry = zipFile.GetEntry(relativePath);

                return entry is null
                    ? new BaseBasicProperties() // Or throw if entry must exist
                    : new AnsiZipFolderBasicProperties(entry);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in GetFolderPropertiesFromEntry: {ex.Message}");
                return new BaseBasicProperties();
            }
            finally
            {
                zipFile?.Close();
            }
        }

        public override IAsyncOperation<BaseBasicProperties> GetBasicPropertiesAsync()
        {
            return AsyncInfo.Run(async (cancellationToken) =>
            {
                if (Path == containerPath) // This is the root zip file itself
                {
                    if (backingFile is not null)
                        return await backingFile.GetBasicPropertiesAsync();
                    
                    // Fallback to direct file system access for the archive file itself
                    var fsFile = await StorageFile.GetFileFromPathAsync(containerPath);
                    return await new SystemStorageFile(fsFile).GetBasicPropertiesAsync();
                }
                return await GetFolderPropertiesFromEntry();
            });
        }


        public override IAsyncOperation<IStorageItem> GetItemAsync(string name)
        {
            return AsyncInfo.Run(async (cancellationToken) =>
                await SafetyExtensions.Wrap<IStorageItem>(async () =>
                {
                    ZipFile zipFile = null;
                    try
                    {
                        zipFile = await OpenZipFileAsync(FileAccess.Read);
                        if (zipFile is null) return null;

                        var itemPathInsideArchive = ZipPathCombine(IO.Path.GetRelativePath(containerPath, Path), name).Replace('\\', '/');
                        ZipEntry entry = zipFile.GetEntry(itemPathInsideArchive);

                        if (entry is null)
                        {
                            // It might be an implicit directory (a file exists like "folder/file.txt" but "folder/" entry doesn't)
                            // Let's check if any entry starts with this path + "/"
                            var directoryPathInsideArchive = itemPathInsideArchive.TrimEnd('/') + "/";
                            if (zipFile.Cast<ZipEntry>().Any(e => e.Name.StartsWith(directoryPathInsideArchive, StringComparison.OrdinalIgnoreCase)))
                            {
                                // Create a synthetic ZipEntry for the folder or find the explicit one if it exists
                                var explicitFolderEntry = zipFile.GetEntry(directoryPathInsideArchive);
                                var folderFullPath = IO.Path.Combine(Path, name);
                                var folder = new AnsiZipStorageFolder(folderFullPath, containerPath, explicitFolderEntry ?? new ZipEntry(directoryPathInsideArchive) { IsDirectory = true, DateTime = DateTime.UtcNow }, backingFile, encoding);
                                ((IPasswordProtectedItem)folder).CopyFrom(this);
                                return folder;
                            }
                            return null;
                        }


                        var fullItemPath = IO.Path.Combine(Path, name);
                        if (entry.IsDirectory)
                        {
                            var folder = new AnsiZipStorageFolder(fullItemPath, containerPath, entry, backingFile, encoding);
                            ((IPasswordProtectedItem)folder).CopyFrom(this);
                            return folder;
                        }
                        else
                        {
                            var file = new AnsiZipStorageFile(fullItemPath, containerPath, entry, backingFile, encoding);
                            ((IPasswordProtectedItem)file).CopyFrom(this);
                            return file;
                        }
                    }
                    finally
                    {
                        zipFile?.Close();
                    }
                }, ((IPasswordProtectedItem)this).RetryWithCredentialsAsync) // Assuming SafetyExtensions and Retry exist
            );
        }

        public override IAsyncOperation<IStorageItem> TryGetItemAsync(string name)
        {
            return AsyncInfo.Run(async (cancellationToken) =>
            {
                try
                {
                    return await GetItemAsync(name);
                }
                catch
                {
                    return null;
                }
            });
        }
        
        // Helper to combine paths for Zip archives (always use '/')
        private string ZipPathCombine(string path1, string path2)
        {
            path1 = path1.Replace('\\', '/').TrimEnd('/');
            path2 = path2.Replace('\\', '/').TrimStart('/');
            return string.IsNullOrEmpty(path1) ? path2 : $"{path1}/{path2}";
        }


        public override IAsyncOperation<IReadOnlyList<IStorageItem>> GetItemsAsync()
        {
            return AsyncInfo.Run(async (cancellationToken) =>
                await SafetyExtensions.Wrap<IReadOnlyList<IStorageItem>>(async () =>
                {
                    ZipFile zipFile = null;
                    var items = new List<IStorageItem>();
                    try
                    {
                        zipFile = await OpenZipFileAsync(FileAccess.Read);
                        if (zipFile is null) return null;

                        var currentRelativePath = string.IsNullOrEmpty(containerPath) || Path == containerPath
                            ? ""
                            : IO.Path.GetRelativePath(containerPath, Path).Replace('\\', '/').TrimEnd('/') + "/";
                        if (currentRelativePath == "/") currentRelativePath = ""; // Root

                        var directChildrenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (ZipEntry entry in zipFile)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue;

                            var entryRelativePath = entry.Name.Replace('\\', '/');

                            if (entryRelativePath.StartsWith(currentRelativePath, StringComparison.OrdinalIgnoreCase) &&
                                entryRelativePath.Length > currentRelativePath.Length)
                            {
                                var remainder = entryRelativePath.Substring(currentRelativePath.Length);
                                var segments = remainder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

                                if (segments.Length > 0)
                                {
                                    var itemName = segments[0];
                                    if (directChildrenNames.Contains(itemName)) continue; // Already added

                                    directChildrenNames.Add(itemName);
                                    var fullItemPath = IO.Path.Combine(Path, itemName);

                                    if (entry.IsDirectory || segments.Length > 1 || remainder.EndsWith("/")) // It's a folder or represents one
                                    {
                                        // Find the explicit entry for the folder if it exists
                                        var folderEntryPath = currentRelativePath + itemName + "/";
                                        var folderEntry = zipFile.GetEntry(folderEntryPath) ?? new ZipEntry(folderEntryPath) { IsDirectory = true, DateTime = entry.DateTime};

                                        var folder = new AnsiZipStorageFolder(fullItemPath, containerPath, folderEntry, backingFile, encoding);
                                        ((IPasswordProtectedItem)folder).CopyFrom(this);
                                        items.Add(folder);
                                    }
                                    else // It's a file
                                    {
                                        var file = new AnsiZipStorageFile(fullItemPath, containerPath, entry, backingFile, encoding);
                                        ((IPasswordProtectedItem)file).CopyFrom(this);
                                        items.Add(file);
                                    }
                                }
                            }
                        }
                        return items;
                    }
                    finally
                    {
                        zipFile?.Close();
                    }
                }, ((IPasswordProtectedItem)this).RetryWithCredentialsAsync)
            );
        }


        public override IAsyncOperation<IReadOnlyList<IStorageItem>> GetItemsAsync(uint startIndex, uint maxItemsToRetrieve)
            => AsyncInfo.Run<IReadOnlyList<IStorageItem>>(async (cancellationToken)
                => (await GetItemsAsync()).Skip((int)startIndex).Take((int)maxItemsToRetrieve).ToList()
            );

        public override IAsyncOperation<BaseStorageFile> GetFileAsync(string name)
            => AsyncInfo.Run<BaseStorageFile>(async (cancellationToken) => await GetItemAsync(name) as AnsiZipStorageFile);

        public override IAsyncOperation<IReadOnlyList<BaseStorageFile>> GetFilesAsync()
            => AsyncInfo.Run<IReadOnlyList<BaseStorageFile>>(async (cancellationToken) => (await GetItemsAsync())?.OfType<AnsiZipStorageFile>().ToList());
        
        public override IAsyncOperation<IReadOnlyList<BaseStorageFile>> GetFilesAsync(CommonFileQuery query)
            => GetFilesAsync(); // No special query handling for zip
        
        public override IAsyncOperation<IReadOnlyList<BaseStorageFile>> GetFilesAsync(CommonFileQuery query, uint startIndex, uint maxItemsToRetrieve)
            => AsyncInfo.Run<IReadOnlyList<BaseStorageFile>>(async (cancellationToken)
                => (await GetFilesAsync()).Skip((int)startIndex).Take((int)maxItemsToRetrieve).ToList()
            );

        public override IAsyncOperation<BaseStorageFolder> GetFolderAsync(string name)
            => AsyncInfo.Run<BaseStorageFolder>(async (cancellationToken) => await GetItemAsync(name) as AnsiZipStorageFolder);
        
        public override IAsyncOperation<IReadOnlyList<BaseStorageFolder>> GetFoldersAsync()
            => AsyncInfo.Run<IReadOnlyList<BaseStorageFolder>>(async (cancellationToken) => (await GetItemsAsync())?.OfType<AnsiZipStorageFolder>().ToList());
        
        public override IAsyncOperation<IReadOnlyList<BaseStorageFolder>> GetFoldersAsync(CommonFolderQuery query)
            => GetFoldersAsync();  // No special query handling for zip

        public override IAsyncOperation<IReadOnlyList<BaseStorageFolder>> GetFoldersAsync(CommonFolderQuery query, uint startIndex, uint maxItemsToRetrieve)
             => AsyncInfo.Run<IReadOnlyList<BaseStorageFolder>>(async (cancellationToken)
                => (await GetFoldersAsync()).Skip((int)startIndex).Take((int)maxItemsToRetrieve).ToList()
            );


        public override IAsyncOperation<BaseStorageFile> CreateFileAsync(string desiredName)
            => CreateFileAsync(desiredName, CreationCollisionOption.FailIfExists);

        public override IAsyncOperation<BaseStorageFile> CreateFileAsync(string desiredName, CreationCollisionOption options)
            => CreateFileAsync(new MemoryStream(), desiredName, options); // Create empty file by default


        public IAsyncOperation<BaseStorageFile> CreateFileAsync(Stream contents, string desiredName, CreationCollisionOption options)
        {
            return AsyncInfo.Run( (cancellationToken) => SafetyExtensions.Wrap<BaseStorageFile>(async () =>
            {
                var relativePath = IO.Path.GetRelativePath(containerPath, Path);
                var entryPath = ZipPathCombine(relativePath, desiredName);

                ZipFile zipFile = null;
                Stream tempStream = null;
                string tempFilePath = null;

                try
                {
                    // SharpZipLib modifies by rewriting. We need a temporary file or memory stream.
                    // For large files, a temp file is better.
                    tempFilePath = IO.Path.GetTempFileName();
                    using (var originalStream = await OpenZipStreamAsync(FileAccessMode.Read)) // Original archive stream
                    {
                        if (originalStream == null && IO.File.Exists(containerPath)) // If backing stream is null but archive exists (e.g. first creation)
                        {
                            // This case is for creating a file in an archive that might be empty or new
                            // and was opened directly via path rather than a backing BaseStorageFile stream.
                        }
                        else if (originalStream == null)
                        {
                             throw new FileNotFoundException("Archive stream could not be opened.");
                        }
                        
                        if(originalStream != null) // Copy existing to temp if it exists
                           await originalStream.CopyToAsync(File.Create(tempFilePath));
                    }

                    zipFile = new ZipFile(tempFilePath); // Open for update
                    zipFile.Password = Credentials.Password; // Required for encrypted archives
                    zipFile.StringCodec = StringCodec.FromEncoding(encoding);


                    zipFile.BeginUpdate();

                    var existingEntry = zipFile.GetEntry(entryPath);
                    if (existingEntry != null)
                    {
                        if (options == CreationCollisionOption.FailIfExists)
                            throw new IOException($"File '{desiredName}' already exists.");
                        if (options == CreationCollisionOption.GenerateUniqueName)
                        {
                            // Simplified: Let's assume FailIfExists or ReplaceExisting for now.
                            // Proper unique name generation would involve checking entryPath_1, entryPath_2, etc.
                            throw new NotSupportedException("GenerateUniqueName is not fully supported for CreateFileAsync in Zip.");
                        }
                        if (options == CreationCollisionOption.ReplaceExisting)
                        {
                            zipFile.Delete(existingEntry);
                        }
                    }

                    var newEntry = new ZipEntry(entryPath)
                    {
                        DateTime = DateTime.Now,
                        IsUnicodeText = (encoding.Equals(Encoding.UTF8)) // Use UTF-8 flag if encoding is UTF-8
                    };
                    
                    // To add content, SharpZipLib needs a source.
                    // IDataSource allows for more complex scenarios, StreamDataSource is simple.
                    var dataSource = new StreamDataSource(contents);
                    zipFile.Add(dataSource, newEntry); // Add with specific entry details
                    
                    zipFile.CommitUpdate();
                    zipFile.Close(); // Close to flush changes to tempFilePath
                    zipFile = null; // Ensure it's not used further

                    // Now, replace the original archive with the temporary one
                    await using (var destStream = await OpenZipStreamAsync(FileAccessMode.ReadWrite))
                    {
                        if (destStream == null) throw new IOException("Could not open archive for writing.");
                        destStream.SetLength(0); // Truncate
                        await using (var sourceStream = File.OpenRead(tempFilePath))
                        {
                            await sourceStream.CopyToAsync(destStream);
                        }
                        await destStream.FlushAsync();
                    }

                    var file = new AnsiZipStorageFile(IO.Path.Combine(Path, desiredName), containerPath, newEntry, backingFile, encoding);
                    ((IPasswordProtectedItem)file).CopyFrom(this);
                    return file;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error creating file in zip: {ex.Message}");
                    if (options == CreationCollisionOption.FailIfExists && ex is ZipException ze && ze.Message.ToLowerInvariant().Contains("already exists"))
                    {
                         return null; // Or rethrow as specific exception
                    }
                    throw; // Rethrow to be caught by SafetyExtensions if needed
                }
                finally
                {
                    zipFile?.Close(); // Ensure closed if an exception occurred mid-operation
                    contents?.Dispose(); // Dispose the provided stream if we are responsible
                    if (tempFilePath != null && File.Exists(tempFilePath))
                    {
                        File.Delete(tempFilePath);
                    }
                }
            }, ((IPasswordProtectedItem)this).RetryWithCredentialsAsync));
        }


        public override IAsyncOperation<BaseStorageFolder> CreateFolderAsync(string desiredName)
            => CreateFolderAsync(desiredName, CreationCollisionOption.FailIfExists);

        public override IAsyncOperation<BaseStorageFolder> CreateFolderAsync(string desiredName, CreationCollisionOption options)
        {
            return AsyncInfo.Run( (cancellationToken) => SafetyExtensions.Wrap<BaseStorageFolder>(async () =>
            {
                var relativePath = IO.Path.GetRelativePath(containerPath, Path);
                var entryPath = ZipPathCombine(relativePath, desiredName).TrimEnd('/') + "/"; // Folder entries must end with '/'

                ZipFile zipFile = null;
                string tempFilePath = null;

                try
                {
                    tempFilePath = IO.Path.GetTempFileName();
                    using (var originalStream = await OpenZipStreamAsync(FileAccessMode.Read))
                    {
                         if (originalStream != null) 
                            await originalStream.CopyToAsync(File.Create(tempFilePath));
                         // else, new archive, tempFilePath will be empty initially
                    }


                    zipFile = new ZipFile(tempFilePath);
                    zipFile.Password = Credentials.Password;
                    zipFile.StringCodec = StringCodec.FromEncoding(encoding);

                    zipFile.BeginUpdate();

                    var existingEntry = zipFile.GetEntry(entryPath);
                    if (existingEntry != null)
                    {
                        if (options == CreationCollisionOption.FailIfExists)
                            throw new IOException($"Folder '{desiredName}' already exists.");
                        if (options == CreationCollisionOption.GenerateUniqueName)
                            throw new NotSupportedException("GenerateUniqueName is not fully supported for CreateFolderAsync in Zip.");
                        if (options == CreationCollisionOption.ReplaceExisting)
                        {
                            // Deleting a folder might require deleting all its contents first.
                            // SharpZipLib's Delete might handle this, or it might only delete the entry itself.
                            // For safety, one might iterate and delete children if problems arise.
                            zipFile.Delete(existingEntry);
                        }
                    }
                    
                    var newEntry = new ZipEntry(entryPath)
                    {
                        DateTime = DateTime.Now,
                        IsDirectory = true, // Important!
                        IsUnicodeText = (encoding.Equals(Encoding.UTF8))
                    };
                    zipFile.Add(newEntry); // Add directory entry

                    zipFile.CommitUpdate();
                    zipFile.Close();
                    zipFile = null;

                    await using (var destStream = await OpenZipStreamAsync(FileAccessMode.ReadWrite))
                    {
                        if (destStream == null) throw new IOException("Could not open archive for writing.");
                        destStream.SetLength(0);
                        await using (var sourceStream = File.OpenRead(tempFilePath))
                        {
                            await sourceStream.CopyToAsync(destStream);
                        }
                        await destStream.FlushAsync();
                    }

                    var folder = new AnsiZipStorageFolder(IO.Path.Combine(Path, desiredName), containerPath, newEntry, backingFile, encoding);
                    ((IPasswordProtectedItem)folder).CopyFrom(this);
                    return folder;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error creating folder in zip: {ex.Message}");
                     if (options == CreationCollisionOption.FailIfExists && ex is ZipException ze && ze.Message.ToLowerInvariant().Contains("already exists"))
                    {
                         return null; 
                    }
                    throw;
                }
                finally
                {
                    zipFile?.Close();
                     if (tempFilePath != null && File.Exists(tempFilePath))
                    {
                        File.Delete(tempFilePath);
                    }
                }
            }, ((IPasswordProtectedItem)this).RetryWithCredentialsAsync));
        }


        public override IAsyncOperation<BaseStorageFolder> MoveAsync(IStorageFolder destinationFolder) => throw new NotSupportedException("MoveAsync is not supported for ZipStorageFolder contents.");
        public override IAsyncOperation<BaseStorageFolder> MoveAsync(IStorageFolder destinationFolder, NameCollisionOption option) => throw new NotSupportedException("MoveAsync is not supported for ZipStorageFolder contents.");

        public override IAsyncAction RenameAsync(string desiredName) => RenameAsync(desiredName, NameCollisionOption.FailIfExists);
        public override IAsyncAction RenameAsync(string desiredName, NameCollisionOption option)
        {
            return AsyncInfo.Run(async (cancellationToken) => await SafetyExtensions.WrapAsync(async () =>
            {
                if (Path == containerPath) // Renaming the archive file itself
                {
                    if (backingFile is not null)
                    {
                        await backingFile.RenameAsync(desiredName, option.ToWindowsNameCollisionOption());
                    }
                    else
                    {
                        var newFullPath = IO.Path.Combine(IO.Path.GetDirectoryName(Path), desiredName);
                        PInvoke.MoveFileFromApp(Path, newFullPath); // Assuming PInvoke helper
                    }
                    // Update internal paths if successful (not shown here, complex state management)
                    return;
                }

                // Renaming an item INSIDE the zip
                // This is complex: requires rebuilding the archive by copying entries,
                // renaming the target entry/entries, and skipping the old ones.
                
                string oldRelativePath = IO.Path.GetRelativePath(containerPath, Path).Replace('\\', '/');
                if (!oldRelativePath.EndsWith('/')) oldRelativePath += "/"; // Ensure it's a directory path for matching children

                string parentDirectoryPath = IO.Path.GetDirectoryName(Path);
                string newAbsolutePath = IO.Path.Combine(parentDirectoryPath, desiredName);
                string newRelativePath = IO.Path.GetRelativePath(containerPath, newAbsolutePath).Replace('\\', '/');
                 if (!newRelativePath.EndsWith('/')) newRelativePath += "/";


                ZipFile zipFile = null;
                string tempInputFilePath = null;
                string tempOutputFilePath = null;

                try
                {
                    tempInputFilePath = IO.Path.GetTempFileName();
                    tempOutputFilePath = IO.Path.GetTempFileName();

                    // 1. Copy current archive to a temp input file
                    using (var originalStream = await OpenZipStreamAsync(FileAccessMode.Read))
                    {
                        if (originalStream == null) throw new IOException("Cannot open archive for reading.");
                        using (var fs = File.Create(tempInputFilePath))
                        {
                            await originalStream.CopyToAsync(fs);
                        }
                    }

                    // 2. Open input temp for reading, output temp for writing
                    using (zipFile = new ZipFile(tempInputFilePath)) // Source
                    using (var zipOutStream = new ZipOutputStream(File.Create(tempOutputFilePath))) // Destination
                    {
                        zipFile.Password = Credentials.Password;
                        zipFile.StringCodec = StringCodec.FromEncoding(encoding);
                        
                        zipOutStream.Password = Credentials.Password;
                        zipOutStream.SetLevel(5); // Default compression
                        ((ZipOutputStream)zipOutStream).StringCodec = StringCodec.FromEncoding(encoding);


                        bool renamedAtLeastOne = false;
                        foreach (ZipEntry entry in zipFile)
                        {
                            string currentEntryName = entry.Name.Replace('\\', '/');
                            string newEntryName = null;

                            if (currentEntryName.Equals(oldRelativePath, StringComparison.OrdinalIgnoreCase) || // Exact folder match
                                currentEntryName.StartsWith(oldRelativePath, StringComparison.OrdinalIgnoreCase)) // Child of folder
                            {
                                newEntryName = newRelativePath + currentEntryName.Substring(oldRelativePath.Length);
                                renamedAtLeastOne = true;
                            }
                            else
                            {
                                newEntryName = currentEntryName; // Keep as is
                            }

                            if (string.IsNullOrEmpty(newEntryName)) continue; // Should not happen with valid zips

                            ZipEntry newEntry = (ZipEntry)entry.Clone(); // Clone properties
                            newEntry.Name = newEntryName;
                            newEntry.IsUnicodeText = (encoding.Equals(Encoding.UTF8));


                            zipOutStream.PutNextEntry(newEntry);

                            if (!entry.IsDirectory)
                            {
                                using (var entryStream = zipFile.GetInputStream(entry))
                                {
                                    await StreamUtils.CopyAsync(entryStream, zipOutStream, new byte[4096], cancellationToken);
                                }
                            }
                            zipOutStream.CloseEntry();
                        }

                        if (!renamedAtLeastOne && option == NameCollisionOption.FailIfExists)
                        {
                            // This could happen if the source path to rename didn't actually exist as an explicit entry
                            // or didn't have children. For simplicity, we'll assume it should have existed.
                            // More robust checking for existence of 'Path' as an item would be needed.
                            System.Diagnostics.Debug.WriteLine($"Warning: Path '{Path}' not found or no entries matched for renaming in zip.");
                        }
                        zipOutStream.Finish();
                    }
                    zipFile = null; // Source closed

                    // 3. Replace original archive with the new temp output file
                    await using (var destStream = await OpenZipStreamAsync(FileAccessMode.ReadWrite))
                    {
                        if (destStream == null) throw new IOException("Could not open archive for final write.");
                        destStream.SetLength(0);
                        await using (var sourceStream = File.OpenRead(tempOutputFilePath))
                        {
                            await sourceStream.CopyToAsync(destStream);
                        }
                        await destStream.FlushAsync();
                    }
                    // TODO: Update 'this.Path' and 'this.Name' if rename was successful. This is tricky as the object instance represents the old path.
                    // This often requires the caller to re-fetch the item with the new name.
                }
                finally
                {
                    zipFile?.Close();
                    if (File.Exists(tempInputFilePath)) File.Delete(tempInputFilePath);
                    if (File.Exists(tempOutputFilePath)) File.Delete(tempOutputFilePath);
                }

            }, ((IPasswordProtectedItem)this).RetryWithCredentialsAsync));
        }


        public override IAsyncAction DeleteAsync() => DeleteAsync(StorageDeleteOption.Default);
        public override IAsyncAction DeleteAsync(StorageDeleteOption option)
        {
            return AsyncInfo.Run(async (cancellationToken) => await SafetyExtensions.WrapAsync(async () =>
            {
                if (Path == containerPath) // Deleting the archive file itself
                {
                    if (backingFile is not null)
                    {
                        await backingFile.DeleteAsync(option);
                    }
                    else if (option == StorageDeleteOption.PermanentDelete || option == StorageDeleteOption.Default) // Default to permanent for direct paths
                    {
                        PInvoke.DeleteFileFromApp(Path); // Assuming PInvoke helper
                    }
                    else
                    {
                        throw new NotSupportedException("Recycle bin not supported for direct archive path deletion without a backing file.");
                    }
                    return;
                }

                // Deleting an item INSIDE the zip
                string relativePathToDelete = IO.Path.GetRelativePath(containerPath, Path).Replace('\\', '/');
                // For folders, ensure it ends with a slash to correctly identify as a prefix for its contents
                if ((await GetBasicPropertiesAsync()).Size == 0 && !relativePathToDelete.EndsWith("/")) // Heuristic for folder
                {
                     // Check if it's a directory by trying to get it as a folder object or checking attributes
                     var item = await TryGetItemAsync(Name); // This is tricky, Name is the last segment of Path
                     // A better way: check if Path represents a directory syntactically or via a prior GetItem call.
                     // For now, assume if it doesn't end with '/', it's a file unless known to be a dir.
                     // This logic is simplified; proper folder detection is needed.
                     // If it's a folder, it should end with '/' for SharpZipLib entry matching.
                     // Let's assume Path is already correct for a folder (ends with /) or it's a file.
                     // Based on the original logic, it seems `Path` is the full path to the item to delete.
                     // We need its relative path to the container.
                }
                // Ensure folder paths end with '/' for matching in SharpZipLib
                bool isLikelyDirectory = this.Attributes.HasFlag(Windows.Storage.FileAttributes.Directory) || relativePathToDelete.EndsWith("/");
                if (isLikelyDirectory && !relativePathToDelete.EndsWith("/"))
                {
                    relativePathToDelete += "/";
                }


                ZipFile zipFile = null;
                string tempFilePath = null;

                try
                {
                    tempFilePath = IO.Path.GetTempFileName();
                    using (var originalStream = await OpenZipStreamAsync(FileAccessMode.Read))
                    {
                        if (originalStream == null) throw new IOException("Cannot open archive for reading.");
                        await originalStream.CopyToAsync(File.Create(tempFilePath));
                    }

                    zipFile = new ZipFile(tempFilePath);
                    zipFile.Password = Credentials.Password;
                    zipFile.StringCodec = StringCodec.FromEncoding(encoding);

                    zipFile.BeginUpdate();
                    
                    var entriesToDelete = new List<ZipEntry>();
                    if(isLikelyDirectory)
                    {
                        foreach(ZipEntry entry in zipFile)
                        {
                            if(entry.Name.Replace('\\', '/').StartsWith(relativePathToDelete, StringComparison.OrdinalIgnoreCase))
                            {
                                entriesToDelete.Add(entry);
                            }
                        }
                    }
                    else
                    {
                        var entry = zipFile.GetEntry(relativePathToDelete);
                        if(entry != null) entriesToDelete.Add(entry);
                    }


                    if (entriesToDelete.Count == 0 && option != StorageDeleteOption.PermanentDelete) // Or if item must exist
                    {
                       // Potentially throw if item not found, or silently succeed.
                       // Original code does not throw if index is empty.
                       System.Diagnostics.Debug.WriteLine($"Item '{Path}' not found in zip for deletion.");
                       zipFile.AbortUpdate(); // No changes needed
                       return; 
                    }
                    
                    foreach(var entry in entriesToDelete)
                    {
                        zipFile.Delete(entry);
                    }

                    zipFile.CommitUpdate();
                    zipFile.Close();
                    zipFile = null;

                    await using (var destStream = await OpenZipStreamAsync(FileAccessMode.ReadWrite))
                    {
                        if (destStream == null) throw new IOException("Could not open archive for writing.");
                        destStream.SetLength(0);
                        await using (var sourceStream = File.OpenRead(tempFilePath))
                        {
                            await sourceStream.CopyToAsync(destStream);
                        }
                        await destStream.FlushAsync();
                    }
                }
                finally
                {
                    zipFile?.Close();
                    if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
                }
            }, ((IPasswordProtectedItem)this).RetryWithCredentialsAsync));
        }


        public override bool AreQueryOptionsSupported(QueryOptions queryOptions) => false;
        public override bool IsCommonFileQuerySupported(CommonFileQuery query) => false;
        public override bool IsCommonFolderQuerySupported(CommonFolderQuery query) => false;

        public override StorageItemQueryResult CreateItemQuery() => throw new NotSupportedException();
        public override BaseStorageItemQueryResult CreateItemQueryWithOptions(QueryOptions queryOptions) => new(this, queryOptions); // Assuming BaseStorageItemQueryResult exists

        public override StorageFileQueryResult CreateFileQuery() => throw new NotSupportedException();
        public override StorageFileQueryResult CreateFileQuery(CommonFileQuery query) => throw new NotSupportedException();
        public override BaseStorageFileQueryResult CreateFileQueryWithOptions(QueryOptions queryOptions) => new(this, queryOptions); // Assuming BaseStorageFileQueryResult exists

        public override StorageFolderQueryResult CreateFolderQuery() => throw new NotSupportedException();
        public override StorageFolderQueryResult CreateFolderQuery(CommonFolderQuery query) => throw new NotSupportedException();
        public override BaseStorageFolderQueryResult CreateFolderQueryWithOptions(QueryOptions queryOptions) => new(this, queryOptions); // Assuming BaseStorageFolderQueryResult exists

        public override IAsyncOperation<StorageItemThumbnail> GetThumbnailAsync(ThumbnailMode mode)
        {
            return AsyncInfo.Run(async (cancellationToken) =>
            {
                if (Path != containerPath || backingFile == null) // Thumbnails only for the archive file itself
                {
                    // For virtual folders inside, try to get a generic folder icon
                    var genericFolder = await StorageFolder.GetFolderFromPathAsync( // This is a placeholder path
                        System.IO.Path.Combine(Package.Current.InstalledLocation.Path, "Assets") 
                        ).AsTask().ConfigureAwait(false);
                    if(genericFolder != null)
                         return await genericFolder.GetThumbnailAsync(mode);
                    return null;
                }
                 if (backingFile != null && backingFile is IStorageFile actualFile) // If backingFile is a real file
                    return await actualFile.GetThumbnailAsync(mode);

                var zipStorageFile = await StorageFile.GetFileFromPathAsync(Path); // Path to the .zip file
                return await zipStorageFile.GetThumbnailAsync(mode);
            });
        }
        public override IAsyncOperation<StorageItemThumbnail> GetThumbnailAsync(ThumbnailMode mode, uint requestedSize)
        {
             return AsyncInfo.Run(async (cancellationToken) =>
            {
                if (Path != containerPath || backingFile == null)
                {
                     var genericFolder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.Combine(Package.Current.InstalledLocation.Path, "Assets")).AsTask().ConfigureAwait(false);
                     if(genericFolder != null)
                         return await genericFolder.GetThumbnailAsync(mode, requestedSize);
                    return null;
                }
                if (backingFile != null && backingFile is IStorageFile actualFile)
                    return await actualFile.GetThumbnailAsync(mode, requestedSize);

                var zipStorageFile = await StorageFile.GetFileFromPathAsync(Path);
                return await zipStorageFile.GetThumbnailAsync(mode, requestedSize);
            });
        }
        public override IAsyncOperation<StorageItemThumbnail> GetThumbnailAsync(ThumbnailMode mode, uint requestedSize, ThumbnailOptions options)
        {
             return AsyncInfo.Run(async (cancellationToken) =>
            {
                if (Path != containerPath || backingFile == null)
                {
                    var genericFolder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.Combine(Package.Current.InstalledLocation.Path, "Assets")).AsTask().ConfigureAwait(false);
                     if(genericFolder != null)
                        return await genericFolder.GetThumbnailAsync(mode, requestedSize, options);
                    return null;
                }
                 if (backingFile != null && backingFile is IStorageFile actualFile)
                    return await actualFile.GetThumbnailAsync(mode, requestedSize, options);

                var zipStorageFile = await StorageFile.GetFileFromPathAsync(Path);
                return await zipStorageFile.GetThumbnailAsync(mode, requestedSize, options);
            });
        }


        private static async Task<bool> CheckAccessAsync(string path, Encoding encoding)
        {
            return await SafetyExtensions.IgnoreExceptions(async () =>
            {
                // OpenFileForRead might not be necessary if StorageFile.GetFileFromPathAsync works
                // For direct path, try to open with FileStream first.
                FileStream stream = null;
                try
                {
                    stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return CheckAccess(stream, null, encoding); // No password for initial check
                }
                catch (Exception ex)
                {
                     System.Diagnostics.Debug.WriteLine($"CheckAccessAsync for path '{path}' failed: {ex.Message}");
                    return false;
                }
                finally
                {
                    stream?.Dispose();
                }
            });
        }

        private static bool CheckAccess(Stream stream, string password, Encoding encoding)
        {
            ZipFile zipFile = null;
            try
            {
                // Must be seekable for SharpZipLib's ZipFile constructor
                if (!stream.CanSeek)
                {
                    var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    ms.Position = 0;
                    stream = ms; // Replace with seekable memory stream
                } else {
                    stream.Position = 0; // Reset position
                }

                zipFile = new ZipFile(stream);
                zipFile.Password = password; // Can be null
                zipFile.StringCodec = StringCodec.FromEncoding(encoding);

                // Try to access an entry or count them
                return zipFile.Count >= 0; // A simple check, implies headers could be read.
            }
            catch (ZipException ze)
            {
                // "Wrong Password?" or "Header checksum illegal" might indicate password issue or corruption
                if (ze.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || 
                    ze.Message.Contains("Header checksum", StringComparison.OrdinalIgnoreCase))
                    return true; // Still "accessible" in the sense that it's a zip, but needs password
                return false; // Other ZipExceptions usually mean not a valid zip or badly corrupted
            }
            catch(Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CheckAccess stream failed: {ex.Message}");
                return false;
            }
            finally
            {
                // ZipFile constructor takes ownership if `isStreamOwner` is true (default).
                // If we created a MemoryStream wrapper, it will be disposed by ZipFile.
                // If the original stream was passed and ZipFile took ownership, it's also handled.
                // If ZipFile didn't take ownership (e.g. if we used a constructor overload that allows it),
                // then we might need to dispose `stream` here IF it was the one we created (MemoryStream).
                // However, the standard ZipFile(Stream) constructor makes ZipFile the owner.
                zipFile?.Close(); // This will also close the underlying stream.
            }
        }

        private static async Task<bool> CheckAccessAsync(BaseStorageFile file, Encoding encoding)
        {
            return await SafetyExtensions.IgnoreExceptions(async () =>
            {
                using var ras = await file.OpenReadAsync();
                using var stream = ras.AsStreamForRead(); // .NET stream
                return CheckAccess(stream, null, encoding); // No password for initial check
            });
        }


        public static Task<bool> InitArchive(string path, Encoding encoding) // Simplified, format is always Zip
        {
            return SafetyExtensions.IgnoreExceptions(async () =>
            {
                FileStream stream = null;
                try
                {
                    // Create or truncate the file
                    stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                    return await InitArchive(stream, encoding);
                }
                finally
                {
                    stream?.Dispose();
                }
            });
        }
        public static Task<bool> InitArchive(IStorageFile file, Encoding encoding)
        {
            return SafetyExtensions.IgnoreExceptions(async () =>
            {
                using var fileStream = await file.OpenAsync(FileAccessMode.ReadWrite);
                await using var stream = fileStream.AsStream(); // .NET stream, ensure it's seekable or wrap
                return await InitArchive(stream, encoding);
            });
        }
        private static async Task<bool> InitArchive(Stream stream, Encoding encoding)
        {
            // Ensure stream is seekable and writable
            if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable for initializing Zip archive.", nameof(stream));
            if (!stream.CanWrite) throw new ArgumentException("Stream must be writable for initializing Zip archive.", nameof(stream));

            stream.SetLength(0); // Clear the stream/file
            ZipFile zipFile = null;
            try
            {
                // The simplest way to create an empty valid zip is to use ZipOutputStream
                // and just Finish it. Or ZipFile.Create, BeginUpdate, CommitUpdate.
                using (var zipOut = new ZipOutputStream(stream))
                {
                    ((ZipOutputStream)zipOut).IsStreamOwner = false; // We manage the input stream
                    ((ZipOutputStream)zipOut).StringCodec = StringCodec.FromEncoding(encoding);
                    zipOut.SetLevel(0); // No compression for an empty archive
                    zipOut.Finish(); // Writes EOCD record
                }
                await stream.FlushAsync();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error initializing archive: {ex.Message}");
                return false;
            }
            finally
            {
                // zipFile?.Close(); // ZipOutputStream handles its own closing/finishing
            }
        }


        private async Task<ZipFile> OpenZipFileAsync(FileAccess desiredAccess)
        {
            Stream stream = await OpenZipStreamAsync(desiredAccess == FileAccess.ReadWrite || desiredAccess == FileAccess.Write ? FileAccessMode.ReadWrite : FileAccessMode.Read);
            if (stream is null) return null;

            // ZipFile requires a seekable stream. If not, buffer it.
            if (!stream.CanSeek)
            {
                var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                stream.Dispose(); // Dispose original non-seekable stream
                ms.Position = 0;
                stream = ms;
            }
            else
            {
                 stream.Position = 0; // Ensure stream is at the beginning
            }


            var zipFile = new ZipFile(stream); // Takes ownership of the stream by default
            zipFile.Password = Credentials.Password;
            zipFile.StringCodec = StringCodec.FromEncoding(encoding);
            return zipFile;
        }

        // Opens the raw stream to the archive file.
        // The caller is responsible for managing this stream if ZipFile doesn't take ownership,
        // or for passing it to ZipFile which will then own it.
        private IAsyncOperation<Stream> OpenZipStreamAsync(FileAccessMode accessMode)
        {
            return AsyncInfo.Run<Stream>(async (cancellationToken) =>
            {
                try
                {
                    if (backingFile is not null)
                    {
                        var uwpStream = await backingFile.OpenAsync(accessMode);
                        return accessMode == FileAccessMode.Read ? uwpStream.AsStreamForRead() : uwpStream.AsStream();
                    }
                    else
                    {
                        // Direct file path access
                        return new FileStream(containerPath,
                            accessMode == FileAccessMode.Read ? FileMode.Open : FileMode.OpenOrCreate,
                            accessMode == FileAccessMode.Read ? System.IO.FileAccess.Read : System.IO.FileAccess.ReadWrite,
                            accessMode == FileAccessMode.Read ? FileShare.Read : FileShare.None);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to open zip stream for '{containerPath}': {ex.Message}");
                    return null;
                }
            });
        }
        
        // This helper is for StreamDataSource for SharpZipLib, making a stream "static" (non-disposing)
        private class StreamDataSource : IStaticDataSource
        {
            private readonly Stream _stream;
            public StreamDataSource(Stream stream) { _stream = stream; }
            public Stream GetSource() => _stream;
        }


        // Removed FetchZipIndex as SharpZipLib operations (like Delete with Begin/CommitUpdate)
        // often handle indexing internally or operate on entry names.
        // If specific index-based modification was key to 7zip's ModifyArchive, that paradigm is different.

        // IPasswordProtectedItem implementation
        void IPasswordProtectedItem.CopyFrom(IPasswordProtectedItem دیگر)
        {
            this.Credentials = new StorageCredential(دیگر.Credentials?.Password);
            this.PasswordRequestedCallback = دیگر.PasswordRequestedCallback;
        }

        async Task<bool> IPasswordProtectedItem.IsPasswordCorrectAsync(string password)
        {
             if (string.IsNullOrEmpty(password) && Credentials.IsPasswordEmpty) return true; // No password set, and none provided
             if (string.IsNullOrEmpty(password)) return false; // Password expected but none provided

            Stream stream = null;
            try
            {
                stream = await OpenZipStreamAsync(FileAccessMode.Read);
                if (stream == null) return string.IsNullOrEmpty(password); // Cannot check if stream fails

                return CheckAccess(stream, password, encoding);
            }
            catch
            {
                return false;
            }
            finally
            {
                stream?.Dispose(); // We opened it directly, so we manage it here.
            }
        }


        private sealed class AnsiZipFolderBasicProperties : BaseBasicProperties
        {
            private readonly ZipEntry _entry;

            public AnsiZipFolderBasicProperties(ZipEntry entry) => _entry = entry;

            // For folders in ZIP, size is often 0.
            // DateModified might be the only relevant field from ZipEntry.
            public override ulong Size => _entry.IsDirectory ? 0 : (ulong)_entry.Size; // Typically 0 for directories in zip
            public override DateTimeOffset DateModified => _entry.DateTime > DateTime.MinValue ? new DateTimeOffset(_entry.DateTime) : DateTimeOffset.MinValue;
            public override DateTimeOffset ItemDate => DateTimeOffset.MinValue; // Not typically used for zip entries
            // DateCreated is not reliably stored for individual entries in all zip formats/tools.
            // Use DateModified as a fallback or a fixed value.
            public override DateTimeOffset DateCreated => _entry.DateTime > DateTime.MinValue ? new DateTimeOffset(_entry.DateTime) : DateTimeOffset.MinValue;
        }
    }
