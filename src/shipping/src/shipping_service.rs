// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

use actix_web::{post, web, HttpResponse, Responder};
use std::time::Duration;
use rand::Rng;
use tracing::{info, warn};
use opentelemetry::trace::{Status, TraceContextExt};
use opentelemetry::Context as OtelContext;
use opentelemetry::KeyValue;

mod quote;
use quote::create_quote_from_count;

mod tracking;
use tracking::create_tracking_id;

mod shipping_types;
pub use shipping_types::*;

const NANOS_MULTIPLE: u32 = 10000000u32;

#[post("/get-quote")]
pub async fn get_quote(req: web::Json<GetQuoteRequest>) -> impl Responder {
    let itemct: u32 = req.items.iter().map(|item| item.quantity as u32).sum();

    let quote = match create_quote_from_count(itemct).await {
        Ok(q) => q,
        Err(e) => {
            return HttpResponse::InternalServerError().body(format!("Failed to get quote: {}", e));
        }
    };

    let reply = GetQuoteResponse {
        cost_usd: Some(Money {
            currency_code: "USD".into(),
            units: quote.dollars,
            nanos: quote.cents * NANOS_MULTIPLE,
        }),
    };

    info!(
        name = "SendingQuoteValue",
        quote.dollars = quote.dollars,
        quote.cents = quote.cents,
        message = "Sending Quote"
    );

    HttpResponse::Ok().json(reply)
}

#[post("/ship-order")]
pub async fn ship_order(req: web::Json<ShipOrderRequest>) -> impl Responder {
    // Synthetic scenario: 15% of orders take 2-3s and exceed the caller's deadline.
    let delay_ms: u64 = {
        let mut rng = rand::thread_rng();
        if rng.gen_range(0..100) < 15 {
            rng.gen_range(2000..3000)
        } else {
            rng.gen_range(5..50)
        }
    };

    tokio::time::sleep(Duration::from_millis(delay_ms)).await;

    if delay_ms > 500 {
        let cx = OtelContext::current();
        let span = cx.span();
        span.set_attribute(KeyValue::new(
            "app.shipping.processing_ms",
            delay_ms as i64,
        ));
        span.set_status(Status::error(format!(
            "processing exceeded SLO: {}ms",
            delay_ms
        )));

        let country = req
            .address
            .as_ref()
            .map(|a| a.country.as_str())
            .unwrap_or("unknown");
        warn!(
            processing_ms = delay_ms,
            address_country = country,
            "shipping handler took {}ms — likely past caller deadline",
            delay_ms
        );
    }

    let tid = create_tracking_id();
    info!(
        name = "CreatingTrackingId",
        tracking_id = tid.as_str(),
        message = "Tracking ID Created"
    );
    HttpResponse::Ok().json(ShipOrderResponse { tracking_id: tid })
}

#[cfg(test)]
mod tests {
    use actix_web::{http::header::ContentType, test, App};

    use super::*;

    #[actix_web::test]
    async fn test_ship_order() {
        let app = test::init_service(App::new().service(ship_order)).await;
        let req = test::TestRequest::post()
            .uri("/ship-order")
            .insert_header(ContentType::json())
            .set_json(&ShipOrderRequest {})
            .to_request();
        let resp = test::call_service(&app, req).await;
        assert!(resp.status().is_success());

        let order: ShipOrderResponse = test::read_body_json(resp).await;
        assert!(!order.tracking_id.is_empty());
    }
}
