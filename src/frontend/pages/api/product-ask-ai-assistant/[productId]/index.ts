// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

import type { NextApiRequest, NextApiResponse } from 'next';
import InstrumentationMiddleware from '../../../../utils/telemetry/InstrumentationMiddleware';
import {AddItemRequest, Empty} from '../../../../protos/demo';
import ProductReviewService from '../../../../services/ProductReview.service';

type TResponse = string | Empty;

const handler = async ({ method, body, query }: NextApiRequest, res: NextApiResponse<TResponse>) => {

    switch (method) {
        case 'POST': {
            const { productId = '' } = query;
            const { question } = body ;

            // Guard against undefined or empty productId
            if (!productId || productId === 'undefined' || productId === '') {
                return res.status(400).json('' as string);
            }

            const response = await ProductReviewService.askProductAIAssistant(productId as string, question as string);

            return res.status(200).json(response);
        }

        default: {
            return res.status(405).send('');
        }
    }
};

export default InstrumentationMiddleware(handler);
