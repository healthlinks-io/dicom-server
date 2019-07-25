﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Net;
using Dicom;
using EnsureThat;
using Microsoft.Health.Dicom.Core.Features.Persistence;
using Microsoft.Health.Dicom.Core.Features.Routing;
using Microsoft.Health.Dicom.Core.Messages.Store;

namespace Microsoft.Health.Dicom.Core.Features.Resources.Store
{
    /// <summary>
    /// http://dicom.nema.org/medical/dicom/current/output/chtml/part18/sect_6.6.html#table_6.6.1-4
    /// </summary>
    internal class StoreTransactionResponseBuilder
    {
        // If any of the failure codes are modified, please check they match the DICOM conformance statement.
        public const ushort SopInstanceAlredyExistsFailureCode = 45070;
        public const ushort MismatchStudyInstanceUIDFailureCode = 43265;
        public const ushort ProcessingFailureCode = 272;
        private readonly DicomDataset _dataset;
        private readonly Uri _baseUri;
        private readonly IDicomRouteProvider _dicomRouteProvider;
        private HttpStatusCode _responseStatusCode = HttpStatusCode.BadRequest;
        private bool _successAdded = false;
        private bool _failureAdded = false;

        public StoreTransactionResponseBuilder(Uri baseUri, IDicomRouteProvider dicomRouteProvider, string studyInstanceUID = null)
        {
            EnsureArg.IsNotNull(baseUri, nameof(baseUri));
            EnsureArg.IsNotNull(dicomRouteProvider, nameof(dicomRouteProvider));

            Uri retrieveUri = string.IsNullOrWhiteSpace(studyInstanceUID) ? null : dicomRouteProvider.GetRetrieveUri(baseUri, new DicomStudy(studyInstanceUID));

            _dataset = new DicomDataset { { DicomTag.RetrieveURL, retrieveUri?.ToString() } };
            _baseUri = baseUri;
            _dicomRouteProvider = dicomRouteProvider;
        }

        public StoreDicomResourcesResponse GetStoreResponse(bool hadAnyUnsupportedContentTypes)
        {
            if (_successAdded || _failureAdded)
            {
                return new StoreDicomResourcesResponse(_responseStatusCode, _dataset);
            }

            // If nothing failed or added we should return no content or unsupported media type.
            return new StoreDicomResourcesResponse(hadAnyUnsupportedContentTypes ? HttpStatusCode.UnsupportedMediaType : HttpStatusCode.NoContent);
        }

        public void AddSuccess(DicomDataset dicomDataset)
        {
            EnsureArg.IsNotNull(dicomDataset, nameof(dicomDataset));

            var dicomInstance = DicomInstance.Create(dicomDataset);
            DicomSequence referencedSopSequence = _dataset.Contains(DicomTag.ReferencedSOPSequence) ?
                                                        _dataset.GetSequence(DicomTag.ReferencedSOPSequence) :
                                                        new DicomSequence(DicomTag.ReferencedSOPSequence);

            referencedSopSequence.Items.Add(new DicomDataset()
            {
                { DicomTag.ReferencedSOPClassUID, dicomDataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty) },
                { DicomTag.ReferencedSOPInstanceUID, dicomInstance.SopInstanceUID },
                { DicomTag.RetrieveURL, _dicomRouteProvider.GetRetrieveUri(_baseUri, dicomInstance).ToString() },
            });

            // If any failures when adding, we return Accepted, otherwise OK.
            _responseStatusCode = _failureAdded ? HttpStatusCode.Accepted : HttpStatusCode.OK;

            _dataset.AddOrUpdate(referencedSopSequence);
            _successAdded = true;
        }

        public void AddFailure(DicomDataset dicomDataset)
            => AddFailure(dicomDataset, ProcessingFailureCode);

        public void AddFailure(DicomDataset dicomDataset, ushort failureReason)
        {
            // If we have added any successfully we return Accepted, otherwise a specific status code.
            _responseStatusCode = _successAdded ? HttpStatusCode.Accepted : HttpStatusCode.Conflict;
            _failureAdded = true;

            DicomSequence failedSopSequence = _dataset.Contains(DicomTag.FailedSOPSequence) ?
                                                    _dataset.GetSequence(DicomTag.FailedSOPSequence) :
                                                    new DicomSequence(DicomTag.FailedSOPSequence);

            if (dicomDataset != null &&
                dicomDataset.TryGetSingleValue(DicomTag.SOPClassUID, out string sopClassUID) &&
                dicomDataset.TryGetSingleValue(DicomTag.SOPInstanceUID, out string sopInstanceUID))
            {
                failedSopSequence.Items.Add(new DicomDataset()
                {
                    { DicomTag.ReferencedSOPClassUID, sopClassUID },
                    { DicomTag.ReferencedSOPInstanceUID, sopInstanceUID },
                    { DicomTag.FailureReason, failureReason },
                });
            }

            _dataset.AddOrUpdate(failedSopSequence);
        }
    }
}