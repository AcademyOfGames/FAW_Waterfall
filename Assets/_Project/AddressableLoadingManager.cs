using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.AddressableAssets.ResourceProviders;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;

public class AddressableLoadingManager : MonoBehaviour
{
    public string addressableLabel;
    // Start is called before the first frame update
    void Start()
    {
    }
    public void InitiateDownload()
    {
        StartCoroutine(DownloadGameData());
    }

    private IEnumerator DownloadGameData()
    {
        AsyncOperationHandle dowonloadHandle = default;
        try
        {
            dowonloadHandle = Addressables.DownloadDependenciesAsync(addressableLabel);
            dowonloadHandle.Completed += OnDownloadComplete;
        }
        catch (Exception e)
        {
            Debug.LogError(e.Message);
        }

        while (!dowonloadHandle.IsDone)
        {
            yield return null;
        }
    }

    void OnDownloadComplete(AsyncOperationHandle handle)
    {
        if (handle.Status == AsyncOperationStatus.Succeeded)
        {
            Addressables.Release(handle);
            Debug.Log("successfully downloaded addressable");
            StartCoroutine(nameof(WaitAndLoadAddressable));
        }
        else
        {
            Addressables.Release(handle);
            Debug.LogError("addressable download not successful");

        }

    }

    IEnumerator WaitAndLoadAddressable()
    {
        yield return new WaitForSeconds(2f);

        AsyncOperationHandle<SceneInstance> loadHandle = default;

        try
        {
            loadHandle = Addressables.LoadSceneAsync(addressableLabel);
        }
        catch
        {
            Debug.LogError("Couldn't load scene async");
            yield break;
        }

        yield return loadHandle;

        if (loadHandle.Status != AsyncOperationStatus.Succeeded)
        {
            Debug.LogError("LoadScene Not Successful");
        }
    }
    // Update is called once per frame
    void Update()
    {

    }
}
