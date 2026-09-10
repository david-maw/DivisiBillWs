![Release Status](https://github.com/david-maw/DivisiBillWs/actions/workflows/azure-functions-app-release-alternate.yml/badge.svg)

# DivisiBillWs

This web service (implemented as Azure functions) provides cloud based services (licensing, storage and OCR) to the DivisiBill app. The web service isn't mandatory, but it does add value. 

DivisiBill is a free .NET MAUI project with only an Android build at present. It is available in the [Play Store](https://play.google.com/store/apps/details?id=com.autoplus.divisibill). DivisiBill is currently
the only product of [AutoPlus](http://autopl.us) and there's more information on the web site [here](https://www.autopl.us/divisibill/index.html) .

## Major Functions

The web service provides the following Azure Functions:

## Information

No license is required to call these functions.

### **Version** (`version`)
Returns comprehensive version and environment information about the application, including application name, version numbers, build time, .NET version, environment details, and the presence of configured services (Cognitive Services, Sentry DSN, Play Store credentials).

### **Status** (`status`)
Returns structured application status information (JSON) including how many scans an OCR license gets and version levels.

## Licensing

No license is required to use the DivisiBill app, but an OCR license is required to use the OCR functionality and a professional subscription is required for cloud storage. The licensing functions handle Android in-app purchases and subscription verification.

### **RecordPurchase** (`RecordPurchaseFunction`)
Records Android purchases in the license store after validating them with the Google Play Store. Handles purchase acknowledgment and licensing.

### **Verify** (`VerifyAndroidPurchase`)
Verifies Android in-app purchases by checking their signature and validating them against the Google Play Store. This ensures that purchase licenses used by the client were issued by the Play Store and are current. Called whenever DivisiBill starts up and periodically as needed.

### **ProPurchaseNotify** (`ProPurchaseNotify`)
Handles notifications related to Pro license purchases and user account migrations. Only used for debugging.

## OCR

Requires an OCR license.

### **Scan** (`scan`)
Performs Optical Character Recognition (OCR) on uploaded images. Requires OCR licensing and processes receipt/bill images to extract text data. Includes license verification against the Play Store.

## Storage

All these functions require a professional license.

### **File** (`file`)
Handles file storage operations in Azure Blob Storage. Supports uploading, downloading, and deleting image files with automatic handling of encrypted files and deletion history tracking. Used for bill images (jpg files). Files may be encrypted in which case .enc is appended to the end of the existing file suffix (so usually .jpg.enc). The encryption key is stored in the application and is unique to each user. The encryption key is never sent to the cloud.

### **Meal** (`MealFunction` and `Meals`)
Provides CRUD (Create, Read, Update, Delete) operations for meal(bill) records. Users can manage individual meal entries and retrieve or delete all meals in their collection. The app uses this functionality to store copies of all meals.

### **PersonList** (`PersonListFunction` and `PersonLists`)
Provides CRUD operations for person lists used to track the list of possible individuals in split expense scenarios. Supports individual list operations and bulk retrieval/deletion of all stored lists.

### **VenueList** (`VenueListFunction` and `VenueLists`)
Provides CRUD operations for venue (location/restaurant) lists. Similar to PersonList, it manages lists of venues for expense tracking.

### **Cleanup** (`CleanupFunction` and `ManualCleanup`)
Automated maintenance function that runs weekly to clean up deleted images from blob storage (items older than 90 days) and expired user data. It can be initiated manually for debugging since it only runs once a week by default.

