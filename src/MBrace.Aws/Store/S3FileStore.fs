﻿namespace MBrace.Aws.Store

open System
open System.Collections.Generic
open System.IO
open System.Runtime.Serialization

open Amazon.S3
open Amazon.S3.Model

open MBrace.Core.Internals
open MBrace.Aws.Runtime

[<AutoOpen>]
module private S3Utils =
    let normalizeDirPath (dir : string) =
        if dir.EndsWith "/" then dir else dir + "/"

//[<Sealed>]
//type internal S3WriteStream () =
//    inherit Stream()
//
//    let inner = new MemoryStream()
//
//    override __.CanRead    = false
//    override __.CanSeek    = false
//    override __.CanWrite   = true
//    override __.CanTimeout = true
//
//    override __.Length = inner.Length
//    override __.Position 
//        with get () = inner.Position 
//        and  set x  = inner.Position <- x
//
//    override __.SetLength _ = raise <| NotSupportedException()
//    override __.Seek (_, _) = raise <| NotSupportedException()
//    override __.Read (_, _, _) = raise <| NotSupportedException()
//    override __.Write (buffer, offset, count) = inner.Write(buffer, offset, count)
//    override __.Flush() = inner.Flush()

[<Sealed; DataContract>]
type S3FileStore private 
        (account    : AwsS3Account, 
         bucketName : string, 
         defaultDir : string) =
    [<DataMember(Name = "S3Account")>]
    let account = account
    
    [<DataMember(Name = "BucketName")>]
    let bucketName = bucketName

    [<DataMember(Name = "DefaultDir")>]
    let defaultDir = defaultDir
    
    let getObjMetadata path = async {
        let req = GetObjectMetadataRequest(BucketName = bucketName, Key = path)
        return! account.S3Client.GetObjectMetadataAsync(req)
                |> Async.AwaitTaskCorrect
    }

    let listObjects prefix nextMarker = async {
        let req = ListObjectsRequest(
                    BucketName = bucketName,
                    Prefix     = prefix,
                    Delimiter  = "/",
                    Marker     = nextMarker)
        return! account.S3Client.ListObjectsAsync(req) 
                |> Async.AwaitTaskCorrect
    }

    let enumerateDir directory map = async {
            let prefix  = normalizeDirPath directory
            let results = ResizeArray<string>()            

            let rec aux nextMarker = async {
                let! res = listObjects prefix nextMarker
                map res |> results.AddRange
                if res.NextMarker = null then return ()
                else return! aux res.NextMarker
            }

            do! aux null
            return Seq.toArray results
        }

//    let enumerateFiles directory = async {
//        let 
//    }

    interface ICloudFileStore with
        member __.Name = "MBrace.Aws.Store.S3FileStore"
        member __.Id = sprintf "arn:aws:s3::%s" bucketName
        member __.IsCaseSensitiveFileSystem = false
        
        //#region Directory Operations
        
        member __.RootDirectory = "/"

        member __.GetDirectoryName(path) = Path.GetDirectoryName path

        member __.GetRandomDirectoryName() = Guid.NewGuid().ToString()

        member __.DirectoryExists(directory) = async {
            let prefix = normalizeDirPath directory
            let req = ListObjectsRequest(
                        BucketName = bucketName, 
                        Prefix = prefix)
            let! res = account.S3Client.ListObjectsAsync req
                       |> Async.AwaitTaskCorrect
            return res.S3Objects.Count > 0
        }

        member __.CreateDirectory(directory) = async {
            let key = normalizeDirPath directory
            let req = PutObjectRequest(BucketName = bucketName, Key = key)
            do! account.S3Client.PutObjectAsync req
                |> Async.AwaitTaskCorrect
                |> Async.Ignore
        }

        member __.DefaultDirectory = defaultDir

        member __.DeleteDirectory(directory, recursiveDelete) = async {
            
//            let req = DeleteObjectsRequest(BucketName = bucketName)
//            req.Objects.Add(new KeyVersion())
//
//            // TODO : handle partial failures
//            do! account.S3Client.DeleteObjectsAsync(req) 
//                |> Async.AwaitTaskCorrect
//                |> Async.Ignore
            return ()
        }

        member __.EnumerateDirectories(directory) = 
            enumerateDir directory (fun res -> res.CommonPrefixes)

        member __.EnumerateFiles(directory) =
            let map = fun (res : ListObjectsResponse) -> 
                res.S3Objects 
                |> Seq.filter (fun obj -> not <| obj.Key.EndsWith "/")
                |> Seq.map (fun obj -> obj.Key)
            enumerateDir directory map         

        //#endregion

        //#region File Operations

        member __.GetFileName(path) = Path.GetFileName(path)

        member __.DeleteFile(path) = async {
            let req = DeleteObjectRequest(BucketName = bucketName, Key = path)
            do! account.S3Client.DeleteObjectAsync(req) 
                |> Async.AwaitTaskCorrect
                |> Async.Ignore
        }
        
        member __.DownloadToLocalFile(cloudSourcePath, localTargetPath) = async {
            do! account.S3Client.DownloadToFilePathAsync(
                    bucketName, 
                    cloudSourcePath, 
                    localTargetPath, 
                    Dictionary<string, obj>())
                |> Async.AwaitTaskCorrect
        }

        member __.DownloadToStream(path, stream) = async {
            let! objStream = 
                account.S3Client.GetObjectStreamAsync(
                    bucketName, path, Dictionary<string, obj>())
                |> Async.AwaitTaskCorrect

            do! objStream.CopyToAsync(stream)
                |> Async.AwaitTaskCorrect
        }

        member this.FileExists(path) = async {
            let! etag = (this :> ICloudFileStore).TryGetETag(path)
            return etag.IsSome
        }
        
        member __.GetFileSize(path) = async {
            let! res = getObjMetadata path
            return res.ContentLength
        }
        member __.GetLastModifiedTime(path, isDirectory) = failwith "Not implemented yet"
                
        member __.IsPathRooted(path) = path.Contains "/" |> not
        
        member __.ReadETag(path, etag) = async {
            let props = Dictionary<string, obj>()
            props.["IfMatch"] <- etag

            let! res = 
                account.S3Client.GetObjectStreamAsync(
                    bucketName, 
                    path, 
                    props) 
                |> Async.AwaitTaskCorrect
                |> Async.Catch

            match res with
            | Choice1Of2 res -> return Some res
            | _ -> return None
        }
        
        member __.TryGetETag(path) = async {
            let! res = getObjMetadata path |> Async.Catch
            match res with
            | Choice1Of2 res -> return Some res.ETag
            | _ -> return None
        }

        member __.UploadFromLocalFile(localSourcePath, cloudTargetPath) = async {
            do! account.S3Client.UploadObjectFromFilePathAsync(
                    bucketName, 
                    cloudTargetPath, 
                    localSourcePath, 
                    Dictionary<string, obj>())
                |> Async.AwaitTaskCorrect
        }

        member __.UploadFromStream(path, stream) = async {
            do! account.S3Client.UploadObjectFromStreamAsync(
                    bucketName,
                    path,
                    stream,
                    Dictionary<string, obj>())
                |> Async.AwaitTaskCorrect
        }

        member this.WriteETag(path, writer) = async {
            let! metaRes = getObjMetadata path
            let! result = async {
                use stream = new MemoryStream()
                let! result = writer(stream)
                do! (this :> ICloudFileStore).UploadFromStream(path, stream)
                return result
            }

            return metaRes.ETag, result
        }
        
        //#endregion

        member __.Combine(paths) = Path.Combine paths

        member __.BeginRead(path) = async {
            return! account.S3Client.GetObjectStreamAsync(
                        bucketName, 
                        path, 
                        Dictionary<string, obj>()) 
                    |> Async.AwaitTaskCorrect
        }

        member __.BeginWrite(path) = failwith "Not implemented yet"

        member __.WithDefaultDirectory(directory) = 
            new S3FileStore(account, bucketName, directory) :> _