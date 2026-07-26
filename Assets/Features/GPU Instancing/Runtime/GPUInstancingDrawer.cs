using UnityEngine;

namespace GPUDriven
{
    public static class GPUInstancingDrawer
    {
        // 如果 GPU buffer 未被创建，那么创建它
        public static void CheckAndInit(InstanceData instanceData)
        {
            if (instanceData.matrixBuffer != null && instanceData.validMatrixBuffer != null &&
                instanceData.argsBuffer != null) return;

            var bufSize = Mathf.Max(instanceData.capacity, instanceData.instanceCount, 64);
            var sizeofMatrix4X4 = 4 * 4 * 4;
            instanceData.matrixBuffer = new ComputeBuffer(bufSize, sizeofMatrix4X4);
            instanceData.validMatrixBuffer = new ComputeBuffer(bufSize, sizeofMatrix4X4,
                ComputeBufferType.Append);
            instanceData.argsBuffer = new ComputeBuffer(5, sizeof(uint), ComputeBufferType.IndirectArguments);

            // 传变换矩阵到 GPU（CPU 路径）；GPU gen 路径下 mats 为空，由 gen CS 直接写入
            if (instanceData.mats != null && instanceData.mats.Length > 0)
                instanceData.matrixBuffer.SetData(instanceData.mats);

            var args = new uint[5] { 0, 0, 0, 0, 0 }; // 绘制参数
            if (instanceData.mesh != null)
            {
                args[0] = instanceData.mesh.GetIndexCount(instanceData.subMeshIndex);
                args[1] = 0;
                args[2] = instanceData.mesh.GetIndexStart(instanceData.subMeshIndex);
                args[3] = instanceData.mesh.GetBaseVertex(instanceData.subMeshIndex);
            }

            instanceData.argsBuffer.SetData(args);
        }

        // All-in drawing
        public static void Draw(InstanceData idata)
        {
            if (idata == null) return;
            CheckAndInit(idata);

            var args = new uint[5] { 0, 0, 0, 0, 0 };
            idata.argsBuffer.GetData(args);
            args[1] = (uint)idata.instanceCount;
            idata.argsBuffer.SetData(args);

            idata.material.SetBuffer("_ValidMatrixBuffer", idata.matrixBuffer);

            Graphics.DrawMeshInstancedIndirect(
                idata.mesh,
                idata.subMeshIndex,
                idata.material,
                new Bounds(Vector3.zero, new Vector3(100.0f, 100.0f, 100.0f)),
                idata.argsBuffer);
        }

        public static Vector4[] BoundToPoint(Bounds b)
        {
            var boundingBox = new Vector4[8];
            boundingBox[0] = new Vector4(b.min.x, b.min.y, b.min.z, 1);
            boundingBox[1] = new Vector4(b.max.x, b.max.y, b.max.z, 1);
            boundingBox[2] = new Vector4(boundingBox[0].x, boundingBox[0].y, boundingBox[1].z, 1);
            boundingBox[3] = new Vector4(boundingBox[0].x, boundingBox[1].y, boundingBox[0].z, 1);
            boundingBox[4] = new Vector4(boundingBox[1].x, boundingBox[0].y, boundingBox[0].z, 1);
            boundingBox[5] = new Vector4(boundingBox[0].x, boundingBox[1].y, boundingBox[1].z, 1);
            boundingBox[6] = new Vector4(boundingBox[1].x, boundingBox[0].y, boundingBox[1].z, 1);
            boundingBox[7] = new Vector4(boundingBox[1].x, boundingBox[1].y, boundingBox[0].z, 1);
            return boundingBox;
        }

        // frustum culling
        public static void Draw(InstanceData idata, Camera camera, ComputeShader cs)
        {
            if (idata == null || camera == null || cs == null) return;
            CheckAndInit(idata);

            // 清空绘制计数
            var args = new uint[5] { 0, 0, 0, 0, 0 };
            idata.argsBuffer.GetData(args);
            args[1] = 0;
            idata.argsBuffer.SetData(args);
            idata.validMatrixBuffer.SetCounterValue(0);

            // 计算视锥体平面
            var ps = GeometryUtility.CalculateFrustumPlanes(camera);
            var planes = new Vector4[6];
            for (var i = 0; i < 6; i++)
                // Ax+By+Cz+D --> Vec4(A,B,C,D)
                planes[i] = new Vector4(ps[i].normal.x, ps[i].normal.y, ps[i].normal.z, ps[i].distance);

            // 计算 bounding box
            var bounds = BoundToPoint(idata.mesh.bounds);

            // 传送参数到 compute shader
            var kid = cs.FindKernel("CSMain");
            cs.SetVectorArray("_bounds", bounds);
            cs.SetVectorArray("_planes", planes);
            cs.SetInt("_instanceCount", idata.instanceCount);
            cs.SetBuffer(kid, "_matrixBuffer", idata.matrixBuffer);
            cs.SetBuffer(kid, "_validMatrixBuffer", idata.validMatrixBuffer);
            cs.SetBuffer(kid, "_argsBuffer", idata.argsBuffer);

            // 视锥剔除
            var nDispatch = idata.instanceCount / 128 + 1; // 128 个 instance 一组线程
            cs.Dispatch(kid, nDispatch, 1, 1);

            idata.material.SetBuffer("_validMatrixBuffer", idata.validMatrixBuffer);

            Graphics.DrawMeshInstancedIndirect(
                idata.mesh,
                idata.subMeshIndex,
                idata.material,
                new Bounds(Vector3.zero, new Vector3(100.0f, 100.0f, 100.0f)),
                idata.argsBuffer);
        }

        public static void Draw(InstanceData instanceData, Camera camera, ComputeShader cs, Matrix4x4 vpMatrix4X4,
            RenderTexture hiZTexture)
        {
            if (instanceData == null || camera == null || cs == null)
                return;
            if (instanceData.mesh == null || instanceData.material == null)
                return;
            CheckAndInit(instanceData);
            if (instanceData.matrixBuffer == null || instanceData.validMatrixBuffer == null ||
                instanceData.argsBuffer == null)
                return;

            // 清空绘制计数
            uint[] args = { 0, 0, 0, 0, 0 };
            instanceData.argsBuffer.GetData(args);
            args[1] = 0;
            instanceData.argsBuffer.SetData(args);
            instanceData.validMatrixBuffer.SetCounterValue(0);

            var planes = GeometryUtility.CalculateFrustumPlanes(camera);
            var planesVec = new Vector4[6];
            for (var i = 0; i < 6; i++)
                planesVec[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z,
                    planes[i].distance);

            var bounds = BoundToPoint(instanceData.mesh.bounds);

            var kernelCull = cs.FindKernel("CSCullInstances");

            cs.SetInt("_InstanceCount", instanceData.instanceCount);
            cs.SetVectorArray("_Bounds", bounds);
            cs.SetVectorArray("_Planes", planesVec);
            cs.SetMatrix("_VPMatrix4x4", vpMatrix4X4);
            cs.SetBuffer(kernelCull, "_MatrixBuffer", instanceData.matrixBuffer);
            cs.SetBuffer(kernelCull, "_ArgsBuffer", instanceData.argsBuffer);
            cs.SetBuffer(kernelCull, "_ValidMatrixBuffer", instanceData.validMatrixBuffer);
            if (hiZTexture != null)
            {
                cs.SetInt("_Size", hiZTexture.width);
                cs.SetTexture(kernelCull, "_HiZTexture", hiZTexture);
            }

            instanceData.material.SetBuffer("_ValidMatrixBuffer", instanceData.validMatrixBuffer);

            // 剔除
            var nDispatch = (int)Mathf.Ceil((float)instanceData.instanceCount / 128);
            cs.Dispatch(kernelCull, nDispatch, 1, 1);

            Graphics.DrawMeshInstancedIndirect(
                instanceData.mesh,
                instanceData.subMeshIndex,
                instanceData.material,
                new Bounds(Vector3.zero, Vector3.one * 1000f),
                instanceData.argsBuffer);
        }
    }
}